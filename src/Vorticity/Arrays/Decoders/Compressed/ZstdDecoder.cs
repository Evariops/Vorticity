using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.zstd</c> into a canonical primitive or varbin view. Zstd is an array encoding
/// here rather than segment compression: a column's values sit in one or more frames that may share
/// a trained dictionary, and nulls are not stored at all, so the decompressed stream holds only the
/// valid values and has to be spread back over the null rows.
/// </summary>
/// <remarks>
/// For utf8 and binary the decompressed stream is not an Arrow layout but a bare sequence of
/// little-endian length-prefixed records with no offsets array, walked forward to rebuild the
/// views; the prefixes stay in the data buffer and the views point past them. A single allocation
/// can never exceed <c>int.MaxValue</c> bytes here, so the values always fit one buffer and every
/// view carries buffer index 0, whatever the file declares.
/// </remarks>
internal sealed class ZstdDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.zstd";


    /// <summary>Bytes of the little-endian length prefix in front of every stored value.</summary>
    private const int ValueLengthPrefix = sizeof(uint);

    /// <summary>The compression level a dictionary is created at: the one whose tables are smallest.</summary>
    private const int FastestLevel = 1;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ZstdDecoder Instance = new ZstdDecoder();

    private ZstdDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.zstd"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Zstd;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start: 0, count: length);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only a node of several frames: a range decompresses every frame it overlaps whole, so over a
    /// single frame each window would decompress the entire array. The buffers are the frames and
    /// an optional dictionary, so three of them mean two frames at least, without reading the
    /// metadata the reader asks this before every window.
    /// </remarks>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.BufferCount >= 3 && context.ValidityDecodesRange(in node, 0);
    }

    /// <summary>
    /// Decodes a range of rows from the frames that hold its values: the valid rows before the
    /// range say where its first value is, the frames' value counts which frames hold it, and the
    /// values of the first frame that precede the range are stepped over.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    /// <remarks>
    /// A node of several frames, the ones <see cref="DecodesRange"/> answers for: over one frame a
    /// selection decompresses the whole array, which the reader's retained chunk does once for
    /// every batch rather than once per batch.
    /// </remarks>
    public override bool SelectsWithoutFullDecodeOf(ArrayDecodeContext context, in ArrayNode node) =>
        DecodesRange(context, in node);

    /// <inheritdoc/>
    /// <remarks>A row is reached by inflating the frame that holds it, a block of the writer's rows.</remarks>
    public override bool SelectsByRow => false;

    /// <summary>
    /// Decodes the wanted rows from the frames that hold their values, each frame once: the rows
    /// whose values lie in one frame are one range, decoded as <see cref="DecodeRange"/> decodes it,
    /// and what is gathered out of each range is put back together in order.
    /// </summary>
    /// <inheritdoc/>
    /// <remarks>
    /// Which frame holds a row's value is its rank among the valid rows, counted forward from one
    /// wanted row to the next over the validity decoded once. A null row has no value and goes with
    /// the frame of the value after it; the rows past the last value go with the last range. A node
    /// of one frame has nothing to skip, and is decoded whole.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (wanted.IsEmpty || !DecodesRange(context, in node))
        {
            return base.DecodeSelected(context, in node, dtype, length, wanted);
        }

        bool isPrimitive = dtype.Kind == DTypeKind.Primitive;
        int byteWidth = isPrimitive ? dtype.PType.ByteWidth() : 1;
        Span<ZstdFrameMetadata> stack = stackalloc ZstdFrameMetadata[16];
        using Scratch<ZstdFrameMetadata> scratch = new Scratch<ZstdFrameMetadata>(node.BufferCount, stack);
        ZstdMetadata metadata = Read(in node, dtype, scratch.Span);
        ReadOnlySpan<ZstdFrameMetadata> frames = scratch.Span[..metadata.FrameCount];

        ReadOnlySpan<byte> bits = default;
        int bitOffset = 0;
        bool nullable = node.ChildCount != 0;
        if (nullable)
        {
            int index = context.DecodeWholeChild(in node, 0, context.Types.Bool(Nullability.NonNullable), length);
            CanonicalNode validity = context.Canonical.GetNode(index);
            if (validity.Kind != CanonicalKind.Bool || validity.Length != length)
            {
                CompressedThrow.Format(
                    $"{Id}'s validity decoded to {validity.Length} rows of {validity.Kind} for an array of {length}.");
            }

            bits = validity.Bits.Span;
            bitOffset = validity.BitOffset;
        }

        Span<int> partStack = stackalloc int[16];
        using Scratch<int> parts = new Scratch<int>(wanted.Length, partStack);
        Span<int> rebasedStack = stackalloc int[64];
        using Scratch<int> rebased = new Scratch<int>(wanted.Length, rebasedStack);

        int count = 0;
        int frame = 0;
        long frameStart = 0;
        long frameEnd = FrameValues(frames, 0, dtype, isPrimitive, byteWidth, 0);
        long rank = 0;
        int rankedTo = 0;
        int i = 0;
        while (i < wanted.Length)
        {
            int first = wanted[i];
            rank += nullable ? BitmapKernels.CountSet(bits, bitOffset + rankedTo, first - rankedTo) : first - rankedTo;
            rankedTo = first;
            while (frame < frames.Length && rank >= frameEnd)
            {
                frame++;
                frameStart = frameEnd;
                if (frame < frames.Length)
                {
                    frameEnd += FrameValues(frames, frame, dtype, isPrimitive, byteWidth, 0);
                }
            }

            int firstValue = (int)rank;
            int j = i + 1;
            while (j < wanted.Length)
            {
                int next = wanted[j];
                long nextRank = rank +
                    (nullable ? BitmapKernels.CountSet(bits, bitOffset + rankedTo, next - rankedTo) : next - rankedTo);
                if (frame < frames.Length && nextRank >= frameEnd)
                {
                    break;
                }

                rank = nextRank;
                rankedTo = next;
                j++;
            }

            int span = wanted[j - 1] - first + 1;
            int decoded = Rows(context, in node, dtype, length, first, span, metadata, frames, firstValue, frame, frameStart);
            if (span == j - i)
            {
                parts.Span[count++] = decoded;
            }
            else
            {
                Span<int> local = rebased.Span[..(j - i)];
                for (int k = i; k < j; k++)
                {
                    local[k - i] = wanted[k] - first;
                }

                parts.Span[count++] = Compute.CanonicalFilter.Apply(context.Canonical, decoded, local);
            }

            i = j;
        }

        return count == 1
            ? parts.Span[0]
            : CanonicalConcat.Concat(context, dtype, wanted.Length, parts.Span[..count]);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        // The frame table is sized by the file, and a wide column can declare a great many frames,
        // so it never becomes a managed array: every consumer below takes a span. Small counts fit
        // the stack, which is the common shape, and the pool takes the tail. `ZstdFrameMetadata` is
        // not a primitive, so the rental is wiped on the way back: the struct holds file-supplied
        // integers the next renter has no business reading.
        //
        // Every frame is a buffer, so the buffer count bounds the table without a pass over the
        // metadata to count it first -- a pass a windowed scan would pay once per window.
        Span<ZstdFrameMetadata> stack = stackalloc ZstdFrameMetadata[16];
        using Scratch<ZstdFrameMetadata> scratch = new Scratch<ZstdFrameMetadata>(node.BufferCount, stack);
        ZstdMetadata metadata = Read(in node, dtype, scratch.Span);
        ReadOnlySpan<ZstdFrameMetadata> frames = scratch.Span[..metadata.FrameCount];
        bool ranged = start != 0 || count != length;
        int firstValue = ranged ? ValuesBefore(context, in node, length, start) : 0;
        return Rows(context, in node, dtype, length, start, count, metadata, frames, firstValue, 0, 0);
    }

    /// <summary>The node's metadata, read into <paramref name="table"/> and checked against its children, dtype and buffers.</summary>
    private static ZstdMetadata Read(in ArrayNode node, DType dtype, Span<ZstdFrameMetadata> table)
    {
        // 0 or 1 child; the one child, when present, is the validity bitmap.
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 0, 1, Id);
        if (dtype.Kind is not (DTypeKind.Primitive or DTypeKind.Utf8 or DTypeKind.Binary))
        {
            CompressedThrow.Format(
                $"{Id} stores primitive, utf8 or binary values; this node's dtype is {dtype}.");
        }

        ZstdMetadata metadata = ZstdMetadata.Read(node.Metadata, table);

        // `validate`: the dictionary buffer is present exactly when the metadata declares one, and
        // the frame buffers match the frame metadata one for one.
        int expectedBuffers = metadata.FrameCount + (metadata.DictionarySize != 0 ? 1 : 0);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, expectedBuffers, Id);
        return metadata;
    }

    /// <summary>
    /// Rows <c>[start, start + count)</c>, whose first value is <paramref name="firstValue"/>, out of
    /// the frames from <paramref name="fromFrame"/> on, which <paramref name="covered"/> values precede.
    /// </summary>
    private static int Rows(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        int start,
        int count,
        ZstdMetadata metadata,
        ReadOnlySpan<ZstdFrameMetadata> frames,
        int firstValue,
        int fromFrame,
        long covered)
    {
        bool isPrimitive = dtype.Kind == DTypeKind.Primitive;
        bool hasDictionary = metadata.DictionarySize != 0;
        bool ranged = start != 0 || count != length;
        Validity validity = ranged
            ? context.DecodeValidityRange(in node, 0, dtype.Nullability, length, start, count)
            : context.DecodeValidity(in node, 0, dtype.Nullability, length);
        int valueCount = CountValid(context, validity, count);
        int byteWidth = isPrimitive ? dtype.PType.ByteWidth() : 1;

        int total = PlanFrames(
            frames, dtype, isPrimitive, byteWidth, firstValue, valueCount, fromFrame, covered,
            out int firstFrame, out int usedFrames, out int skipped);

        // The UTF-8 shortcut is decided frame by frame, inside the decompression loop, rather than
        // by one sweep of the finished heap; `Decompress` says why the two are the same predicate.
        bool scanAscii = dtype.Kind == DTypeKind.Utf8;
        VortexBuffer values = Decompress(
            context, in node, metadata, frames, firstFrame, usedFrames, hasDictionary, total, byteWidth,
            scanAscii, out bool allAscii);
        int decompressed = values.Length;
        values = StepOver(values, skipped, isPrimitive, byteWidth);

        return isPrimitive
            ? Scatter(context, dtype, count, validity, values, valueCount, byteWidth)
            : BuildViews(
                context, dtype, count, validity, values, valueCount, scanAscii && !allAscii,
                frames.Slice(firstFrame, usedFrames), skipped, decompressed - values.Length);
    }

    /// <summary>The values the frames hold before row <paramref name="start"/>: its valid rows before it.</summary>
    /// <remarks>
    /// The frames hold the valid values alone, so where a range's first value sits is a count only
    /// the whole validity gives; it is decoded once for every range of the node, as a patch set is.
    /// </remarks>
    private static int ValuesBefore(ArrayDecodeContext context, in ArrayNode node, int length, int start)
    {
        if (node.ChildCount == 0 || start == 0)
        {
            return start;
        }

        int index = context.DecodeWholeChild(in node, 0, context.Types.Bool(Nullability.NonNullable), length);
        CanonicalNode bits = context.Canonical.GetNode(index);
        if (bits.Kind != CanonicalKind.Bool || bits.Length != length)
        {
            CompressedThrow.Format(
                $"{Id}'s validity decoded to {bits.Length} rows of {bits.Kind} for an array of {length}.");
        }

        return context.ValidBefore(in node, bits.Bits.Span, bits.BitOffset, start);
    }

    /// <summary>
    /// The decompressed values past the <paramref name="skipped"/> that precede a range in its first
    /// frame: a fixed width steps over them at once, length-prefixed values one prefix at a time.
    /// </summary>
    private static VortexBuffer StepOver(VortexBuffer values, int skipped, bool isPrimitive, int byteWidth)
    {
        if (skipped == 0)
        {
            return values;
        }

        if (isPrimitive)
        {
            long bytes = (long)skipped * byteWidth;
            if (bytes > values.Length)
            {
                CompressedThrow.Format(
                    $"{Id}'s first frame of a range decompressed {values.Length} bytes; the range starts {bytes} in.");
            }

            return values.Slice((int)bytes);
        }

        ReadOnlySpan<byte> heap = values.Span;
        int offset = 0;
        for (int i = 0; i < skipped; i++)
        {
            if (offset > heap.Length - ValueLengthPrefix)
            {
                CompressedThrow.Format(
                    $"{Id}: the length prefix of value {i} runs past the end of the " +
                    $"{heap.Length}-byte decompressed stream.");
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(heap.Slice(offset, ValueLengthPrefix));
            if (size > (uint)(heap.Length - offset - ValueLengthPrefix))
            {
                CompressedThrow.Format(
                    $"{Id}: value {i} claims {size} bytes at offset {offset + ValueLengthPrefix}, past " +
                    $"the end of the {heap.Length}-byte decompressed stream.");
            }

            offset += ValueLengthPrefix + (int)size;
        }

        return values.Slice(offset);
    }

    /// <summary>
    /// Decides which frames carry values <c>[firstValue, firstValue + valueCount)</c> and how many
    /// bytes they decompress to.
    /// </summary>
    /// <param name="frames">The frame table.</param>
    /// <param name="dtype">The array's dtype, for a message.</param>
    /// <param name="isPrimitive">Whether the values have a fixed width.</param>
    /// <param name="byteWidth">That width.</param>
    /// <param name="firstValue">The first value wanted.</param>
    /// <param name="valueCount">How many are wanted.</param>
    /// <param name="fromFrame">The frame the walk starts at, none before it holding a wanted value.</param>
    /// <param name="covered">The values of the frames before <paramref name="fromFrame"/>.</param>
    /// <param name="firstFrame">The first frame that holds one.</param>
    /// <param name="usedFrames">How many frames from it hold them.</param>
    /// <param name="skipped">The values of the first frame that precede the first wanted.</param>
    /// <remarks>
    /// Upstream walks frames tracking a value cursor and stops once the cursor passes the last
    /// value the slice wants. Unsliced, that window is <c>[0, valueCount)</c>, so the walk keeps
    /// frames until their cumulative value count covers every valid row; a range first steps over
    /// the frames that end before its first value.
    /// </remarks>
    private static int PlanFrames(
        ReadOnlySpan<ZstdFrameMetadata> frames,
        DType dtype,
        bool isPrimitive,
        int byteWidth,
        int firstValue,
        int valueCount,
        int fromFrame,
        long covered,
        out int firstFrame,
        out int usedFrames,
        out int skipped)
    {
        long total = 0;
        long end = (long)firstValue + valueCount;
        firstFrame = fromFrame;
        usedFrames = 0;
        skipped = 0;

        for (int i = fromFrame; i < frames.Length && covered < end; i++)
        {
            long uncompressed = CheckedSize(frames[i].UncompressedSize, i, "uncompressed size");
            long frameValues = FrameValues(frames, i, dtype, isPrimitive, byteWidth, valueCount);
            if (covered + frameValues <= firstValue)
            {
                covered += frameValues;
                firstFrame = i + 1;
                continue;
            }

            if (usedFrames == 0)
            {
                skipped = (int)(firstValue - covered);
            }

            total += uncompressed;
            if (total > int.MaxValue)
            {
                CompressedThrow.Format(
                    $"{Id} frames declare {total} uncompressed bytes, past this reader's limit.");
            }

            covered += frameValues;
            usedFrames++;
        }

        return (int)total;
    }

    /// <summary>The values frame <paramref name="index"/> holds.</summary>
    /// <param name="frames">The frame table.</param>
    /// <param name="index">The frame.</param>
    /// <param name="dtype">The array's dtype, for a message.</param>
    /// <param name="isPrimitive">Whether the values have a fixed width.</param>
    /// <param name="byteWidth">That width.</param>
    /// <param name="valueCount">The values of the whole array, which a lone frame of variable width holds.</param>
    private static long FrameValues(
        ReadOnlySpan<ZstdFrameMetadata> frames, int index, DType dtype, bool isPrimitive, int byteWidth, int valueCount)
    {
        ZstdFrameMetadata frame = frames[index];
        if (frame.ValueCount != 0)
        {
            return CheckedSize(frame.ValueCount, index, "value count");
        }

        if (isPrimitive)
        {
            // Older primitive-only metadata omitted the count; a fixed width makes the byte count
            // an exact value count.
            return CheckedSize(frame.UncompressedSize, index, "uncompressed size") / byteWidth;
        }

        if (frames.Length == 1)
        {
            // The same fallback would read a byte count as a value count for variable-width
            // values. One frame holds everything, so that case is still recoverable.
            return valueCount;
        }

        return CompressedThrow.Format<long>(
            $"{Id} frame {index} of a {dtype.Kind} array declares no value count, and the " +
            "array has more than one frame, so its values cannot be attributed.");
    }

    /// <summary>
    /// Decompresses <paramref name="usedFrames"/> frames from <paramref name="firstFrame"/>, in
    /// order, into one allocation.
    /// </summary>
    /// <remarks>
    /// <c>scanAscii</c> asks, for a utf8 array only, whether every decompressed byte is below 0x80;
    /// <c>allAscii</c> answers it, and is false whenever nothing was asked.
    /// </remarks>
    private static VortexBuffer Decompress(
        ArrayDecodeContext context,
        in ArrayNode node,
        ZstdMetadata metadata,
        ReadOnlySpan<ZstdFrameMetadata> frames,
        int firstFrame,
        int usedFrames,
        bool hasDictionary,
        int total,
        int byteWidth,
        bool scanAscii,
        out bool allAscii)
    {
        // The cap applies here and not one line later: `total` is a file-supplied sum, and a tiny
        // frame can claim to expand to an arbitrary size.
        //
        // The block is left uninitialized, unlike the view buffer below, for a reason the two do
        // not share. The frames are written back to back from offset 0 and the loop refuses to
        // return unless `written == total`, so every byte here is produced before anyone reads it,
        // and nothing rereads a byte it has not written first. A fill would therefore be a whole
        // extra pass over the decompressed size, buying nothing.
        VortexBuffer output = CanonicalSupport.AllocateUninitialized(
            context, total, byteWidth, out Span<byte> destination);
        allAscii = scanAscii;
        if (total == 0)
        {
            return output;
        }

        int firstBuffer = (hasDictionary ? 1 : 0) + firstFrame;
        ZstandardDictionary? dictionary = null;
        ZstandardDecoder? owned = null;
        try
        {
            if (hasDictionary)
            {
                VortexBuffer raw = node.GetBuffer(0);
                if (raw.Length != metadata.DictionarySize)
                {
                    CompressedThrow.Format(
                        $"{Id} declares a {metadata.DictionarySize}-byte dictionary but its " +
                        $"buffer holds {raw.Length}.");
                }

                // Allocates native state, so it is built only for a file that actually carries a
                // dictionary - which no default writer produces - and for that node alone. Every
                // other frame goes through the context's decoder, built once for the scan.
                //
                // Only ever decompressed with, the dictionary is still given a compression table by
                // the platform, sized by the level it is created at: level 1's is the smallest, and
                // building the dictionary and its decoder costs about two thirds of what the
                // default level's does.
                dictionary = ZstandardDictionary.Create(raw.Span, FastestLevel);
                owned = new ZstandardDecoder(dictionary);
            }

            ZstandardDecoder decoder = owned ?? context.Scan.Zstd;
            int written = 0;
            for (int i = 0; i < usedFrames; i++)
            {
                ReadOnlySpan<byte> frame = node.GetBuffer(firstBuffer + i).Span;
                RequireDeclaredContentSize(frame, frames[firstFrame + i].UncompressedSize, firstFrame + i);

                // Bounded by what is left of the planned total, so a frame that expands further
                // than advertised is refused by the decoder rather than overrunning.
                Span<byte> region = destination[written..];
                decoder.Reset();
                System.Buffers.OperationStatus status =
                    decoder.Decompress(frame, region, out _, out int produced);
                if (status != System.Buffers.OperationStatus.Done)
                {
                    CompressedThrow.Format(
                        $"{Id} frame {firstFrame + i} did not decompress into the {region.Length} bytes its " +
                        "metadata left for it.");
                }

                // The ASCII sweep runs here rather than over the finished heap. The frames
                // partition the stream, written back to back from 0 with sizes summing to `total`,
                // so "every frame is ASCII" and "the heap is ASCII" say the same thing and the
                // guarantee handed to `BuildViews` is untouched. Testing each region right after
                // it is written keeps the bytes in cache instead of paying a second pass over the
                // whole heap.
                if (scanAscii && allAscii)
                {
                    allAscii = Ascii.IsValid(region[..produced]);
                }

                written += produced;
            }

            if (written != total)
            {
                CompressedThrow.Format(
                    $"{Id} frames decompressed to {written} bytes; their metadata declared {total}.");
            }
        }
        finally
        {
            owned?.Dispose();
            dictionary?.Dispose();
        }

        return output;
    }

    /// <summary>
    /// Spreads compact values back over their rows, leaving null slots zeroed.
    /// </summary>
    /// <remarks>
    /// The mirror of upstream's <c>PrimitiveData::from_values_byte_buffer</c>, including that a
    /// null row's bytes are zeros rather than whatever was there before.
    /// </remarks>
    private static int Scatter(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        Validity validity,
        VortexBuffer values,
        int valueCount,
        int byteWidth)
    {
        int required = ArrayDecodeContext.CheckedMultiply(valueCount, byteWidth, Id + " values");
        if (values.Length < required)
        {
            CompressedThrow.Format(
                $"{Id} decompressed {values.Length} bytes for {valueCount} values of " +
                $"{byteWidth} bytes each.");
        }

        ValidityMask mask = ValidityMask.From(context, validity);
        if (mask.AllValid)
        {
            // Already dense: the decompressed buffer is the values buffer.
            return context.Canonical.AddPrimitive(
                dtype, length, validity, dtype.PType, values.Slice(0, required));
        }

        int span = ArrayDecodeContext.CheckedMultiply(length, byteWidth, Id + " rows");
        VortexBuffer output = CanonicalSupport.Allocate(
            context, span, byteWidth, out Span<byte> destination);
        ReadOnlySpan<byte> source = values.Span;

        // The width is resolved once here and not per row: a copy whose length is only known at
        // run time costs a call per valid row to move four or eight bytes, which dominates the
        // scatter on a nullable column.
        switch (byteWidth)
        {
            case 1:
                Expand<byte>(source, destination, in mask, length);
                break;
            case 2:
                Expand<ushort>(source, destination, in mask, length);
                break;
            case 4:
                Expand<uint>(source, destination, in mask, length);
                break;
            case 8:
                Expand<ulong>(source, destination, in mask, length);
                break;
            default:
                ExpandWide(source, destination, in mask, length, byteWidth);
                break;
        }

        return context.Canonical.AddPrimitive(dtype, length, validity, dtype.PType, output);
    }

    /// <summary>Spreads a dense run of <typeparamref name="T"/> over the mask's valid rows.</summary>
    /// <typeparam name="T">The value type, chosen from the byte width by the caller.</typeparam>
    /// <remarks>
    /// Sixty-four rows at a time: a word of valid rows is one copy of its values, a word of nulls is
    /// nothing, since the destination is zeroed, and a mixed word of many values writes every one of
    /// its rows, the next value or zero as its bit says, the next value moving on by the bit.
    /// Walking the set bits instead chains each value's row to the bit before it, which pays only
    /// for a word of few values, <see cref="SparseScatter"/> or fewer.
    /// </remarks>
    internal static void Expand<T>(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source);
        Span<T> rows = MemoryMarshal.Cast<byte, T>(destination)[..length];
        ReadOnlySpan<byte> bits = mask.Bits;
        int bitOffset = mask.BitOffset;
        int next = 0;
        bool expands = Expands<T>();
        Compute.WordBytes spread = expands ? Compute.WordBytes.Create() : default;
        for (int row = 0; row < length; row += 64)
        {
            int span = Math.Min(64, length - row);
            ulong full = BitWords.Mask(span);
            ulong word = BitWords.Load(bits, bitOffset + row) & full;
            if (word == full)
            {
                values.Slice(next, span).CopyTo(rows.Slice(row, span));
                next += span;
                continue;
            }

            if (word == 0)
            {
                continue;
            }

            int count = BitOperations.PopCount(word);
            if (next + count > values.Length)
            {
                CompressedThrow.Format($"{Id} has more valid rows than decompressed values.");
            }

            // A whole word whose values can be read a vector at a time is expanded in registers.
            if (expands && span == 64 && next + 64 <= values.Length)
            {
                next = Expanded(values, ref MemoryMarshal.GetReference(rows[row..]), word, next, spread);
                continue;
            }

            next = count > SparseScatter
                ? Scatter(values, ref MemoryMarshal.GetReference(rows[row..]), span, word, next)
                : Walk(values, ref MemoryMarshal.GetReference(rows[row..]), word, next);
        }
    }

    /// <summary>Whether <typeparamref name="T"/>'s lanes expand in registers on this machine.</summary>
    private static bool Expands<T>() =>
        Compute.WordBytes.IsAccelerated && (Unsafe.SizeOf<T>() >= 4 ? Avx512F.IsSupported : Avx512Vbmi2.IsSupported);

    /// <summary>
    /// One mixed word's rows by <c>vpexpand</c>: for each vector of rows, the next values loaded
    /// whole, laid on the rows whose bits are set and zero elsewhere, and the cursor moved on by
    /// the bits' count.
    /// </summary>
    /// <returns>The next value after the word.</returns>
    /// <remarks>
    /// Eight rows a step for eight-byte values, sixteen for four, thirty-two for two, the whole word
    /// for one: a load, an expand and a store, where the scatter writes each row. The expand runs
    /// from a register to a register; it is its form that writes memory that is slow on Zen 4.
    /// The caller has checked that 64 values can be read from <paramref name="next"/>, which covers
    /// every load of the word, each at most a vector past values the word takes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Expanded<T>(ReadOnlySpan<T> values, ref T rows, ulong word, int next, Compute.WordBytes spread)
        where T : unmanaged
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        int lanes = Vector512<T>.Count;
        for (int group = 0; group < 64 / lanes; group++)
        {
            Vector512<T> dense = Vector512.LoadUnsafe(ref value, (nuint)next);
            Vector512<T> mask = spread.Lanes<T>(word, group);
            Vector512<T> laid = Unsafe.SizeOf<T>() switch
            {
                1 => Avx512Vbmi2.Expand(Vector512<byte>.Zero, mask.AsByte(), dense.AsByte()).As<byte, T>(),
                2 => Avx512Vbmi2.Expand(Vector512<ushort>.Zero, mask.AsUInt16(), dense.AsUInt16()).As<ushort, T>(),
                4 => Avx512F.Expand(Vector512<uint>.Zero, mask.AsUInt32(), dense.AsUInt32()).As<uint, T>(),
                _ => Avx512F.Expand(Vector512<ulong>.Zero, mask.AsUInt64(), dense.AsUInt64()).As<ulong, T>(),
            };
            laid.StoreUnsafe(ref rows, (nuint)(group * lanes));
            next += BitOperations.PopCount((word >> (group * lanes)) & (lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1));
        }

        return next;
    }

    /// <summary>
    /// The valid rows of a word at or under which its set bits are walked rather than every row
    /// written: a written row costs about as much as a walked bit whose word holds this many.
    /// </summary>
    private const int SparseScatter = 36;

    /// <summary>
    /// One mixed word's rows: each takes the next value or zero as its bit says, and the next value
    /// moves on by the bit; the value read is held to the last one, so a word's trailing nulls read
    /// nothing past the values.
    /// </summary>
    /// <returns>The next value after the word.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Scatter<T>(ReadOnlySpan<T> values, ref T rows, int span, ulong word, int next)
        where T : unmanaged, IBinaryInteger<T>
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        int last = values.Length - 1;
        for (int k = 0; k < span; k++)
        {
            int bit = (int)(word >> k) & 1;
            // Past the last value by one at most, and then held to it: the sign of last - next.
            int at = next - (int)((uint)(last - next) >> 31);
            Unsafe.Add(ref rows, k) = Unsafe.Add(ref value, at) & (T.Zero - T.CreateTruncating(bit));
            next += bit;
        }

        return next;
    }

    /// <summary>A word of few values: its set bits walked, each taking the next value.</summary>
    /// <returns>The next value after the word.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Walk<T>(ReadOnlySpan<T> values, ref T rows, ulong word, int next)
        where T : unmanaged
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        while (word != 0)
        {
            Unsafe.Add(ref rows, BitOperations.TrailingZeroCount(word)) = Unsafe.Add(ref value, next++);
            word &= word - 1;
        }

        return next;
    }

    /// <summary>The same, for a width no primitive type has. Kept so the switch is total.</summary>
    /// <remarks>
    /// No <c>PType</c> is 3, 5, 6 or 7 bytes wide, so nothing reaches this. It exists because a
    /// <c>default</c> that threw would turn another width into a crash on a file, and one that did
    /// nothing would turn it into silent zeros.
    /// </remarks>
    private static void ExpandWide(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length, int width)
    {
        int next = 0;
        for (int row = 0; row < length; row++)
        {
            if (mask.IsValid(row))
            {
                source.Slice(next * width, width).CopyTo(destination.Slice(row * width, width));
                next++;
            }
        }
    }

    /// <summary>
    /// Rebuilds Arrow views by walking the length-prefixed value stream, then scatters them over
    /// the null slots.
    /// </summary>
    /// <param name="context">The decode context.</param>
    /// <param name="dtype">The array's dtype.</param>
    /// <param name="length">Rows.</param>
    /// <param name="validity">Their validity.</param>
    /// <param name="values">The stream, from the first value wanted.</param>
    /// <param name="valueCount">The valid rows, which is how many values are wanted.</param>
    /// <param name="requireUtf8">Whether each value must be checked as UTF-8.</param>
    /// <param name="frames">The frames the stream was decompressed from, in order.</param>
    /// <param name="skipped">The values of the first frame stepped over before the stream starts.</param>
    /// <param name="stepped">The bytes they took.</param>
    private static int BuildViews(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        Validity validity,
        VortexBuffer values,
        int valueCount,
        bool requireUtf8,
        ReadOnlySpan<ZstdFrameMetadata> frames,
        int skipped,
        int stepped)
    {
        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, Id + " views");

        ValidityMask mask = ValidityMask.From(context, validity);

        // The zero fill stays even though an all-valid array overwrites every view. It is not pure
        // overhead: the sequential fill pulls the view buffer into cache just ahead of the loop
        // that rewrites it, so dropping it trades that fill for a cold miss per row. A null row
        // needs the zeros anyway, since it keeps the empty view the loop skips over.
        VortexBuffer views = CanonicalSupport.Allocate(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        ReadOnlySpan<byte> heap = values.Span;

        // `requireUtf8` is the answer to one sweep of the whole stream, decided frame by frame in
        // `Decompress`, instead of a validation call per row. Every byte below 0x80 is a complete,
        // valid UTF-8 sequence on its own, so an ASCII stream makes every value inside it valid
        // whatever the boundaries, and a validator called twenty bytes at a time never reaches its
        // stride.
        //
        // The four-byte length prefixes sit inside the heap and are not text, but they are ASCII
        // whenever a value is shorter than 128 bytes with three zero bytes above it, which is the
        // shape of essentially every string column. When the sweep does find a high byte, whether
        // a genuinely non-ASCII value or one at least 128 bytes long, the per-row path below runs.

        // Every row valid and no row to validate, the common shape: loops that call nothing, frame
        // by frame and several frames at once where the frames account for the stream, else over
        // the stream as one. Should a prefix or a value run past the stream, it stops short, and the
        // loop below walks the rows again to say which.
        if (mask.AllValid && !requireUtf8
            && (BuildFromFrames(heap, writable, length, frames, skipped, stepped)
                || ViewKernels.BuildFromPrefixed(heap, writable, length) == length))
        {
            return AttachHeap(context, dtype, length, validity, views, values);
        }

        int offset = 0;
        int written = 0;
        for (int row = 0; row < length; row++)
        {
            if (!mask.IsValid(row))
            {
                // Null rows keep BinaryView::empty_view() - the zeros Allocate already wrote.
                continue;
            }

            if (offset + ValueLengthPrefix > heap.Length)
            {
                CompressedThrow.Format(
                    $"{Id}: the length prefix of value {written} runs past the end of the " +
                    $"{heap.Length}-byte decompressed stream.");
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(heap.Slice(offset, ValueLengthPrefix));
            int start = offset + ValueLengthPrefix;
            if (size > (uint)(heap.Length - start))
            {
                CompressedThrow.Format(
                    $"{Id}: value {written} claims {size} bytes at offset {start}, past the end " +
                    $"of the {heap.Length}-byte decompressed stream.");
            }

            ReadOnlySpan<byte> value = heap.Slice(start, (int)size);
            if (requireUtf8 && !Utf8.IsValid(value))
            {
                throw new VortexFormatException($"Row {row} of a Utf8 array is not valid UTF-8.");
            }

            // Buffer index 0 always: the values of a node fit one buffer here, so there is never a
            // second one to point at. The single call covers both the inline and the referenced
            // shape, and writes two registers rather than clearing and copying the view per row;
            // an inline value is read as two words from the stream, where there are twelve bytes.
            Span<byte> view = writable.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            Canonical.ViewKernels.PlaceView(
                ref MemoryMarshal.GetReference(view), ref MemoryMarshal.GetReference(heap), start, (int)size, heap.Length);

            offset = start + (int)size;
            written++;
        }

        if (written != valueCount)
        {
            CompressedThrow.Format(
                $"{Id}: the decompressed frames hold {written} values for the {valueCount} valid " +
                "rows of this array.");
        }

        return AttachHeap(context, dtype, length, validity, views, values);
    }

    /// <summary>
    /// Cuts the stream frame by frame, every frame starting on a value of its own, so that several
    /// frames are cut at once; false when there is one frame, or when the frames' value counts and
    /// sizes do not account for the stream, for the caller to cut it as one.
    /// </summary>
    /// <remarks>
    /// Where a value starts is known only once the length before it is read, so the stream alone is
    /// a chain of dependent loads; the frame table gives every frame's start, which splits it into
    /// independent chains. The table comes from the file and is not trusted: every value must lie in
    /// its frame's bytes, and every frame cut whole must end where its last value does.
    /// </remarks>
    private static bool BuildFromFrames(
        ReadOnlySpan<byte> heap, Span<byte> views, int length, ReadOnlySpan<ZstdFrameMetadata> frames,
        int skipped, int stepped)
    {
        if (frames.Length < 2)
        {
            return false;
        }

        Span<ViewKernels.PrefixedRun> stack = stackalloc ViewKernels.PrefixedRun[16];
        using Scratch<ViewKernels.PrefixedRun> scratch = new Scratch<ViewKernels.PrefixedRun>(frames.Length, stack);
        Span<ViewKernels.PrefixedRun> runs = scratch.Span;

        // In the stream's own terms, which start past the stepped-over values of the first frame.
        long start = -stepped;
        int row = 0;
        int used = 0;
        for (int i = 0; i < frames.Length && row < length; i++)
        {
            long held = (long)frames[i].ValueCount - (i == 0 ? skipped : 0);
            long end = start + (long)frames[i].UncompressedSize;
            if (held <= 0 || end > heap.Length)
            {
                return false;
            }

            int rows = (int)Math.Min(held, length - row);
            runs[used++] = new ViewKernels.PrefixedRun((int)Math.Max(start, 0), (int)end, row, rows, rows == held);
            row += rows;
            start = end;
        }

        return row == length && ViewKernels.BuildFromPrefixedRuns(heap, views, runs[..used]);
    }

    /// <summary>The varbin view node over <paramref name="views"/>, with the heap as its one data buffer unless it is empty.</summary>
    private static int AttachHeap(
        ArrayDecodeContext context, DType dtype, int length, Validity validity, VortexBuffer views, VortexBuffer values)
    {
        if (values.Length == 0)
        {
            return context.Canonical.AddVarBinView(dtype, length, validity, views, default);
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = values;
        return context.Canonical.AddVarBinView(dtype, length, validity, views, single);
    }

    /// <summary>
    /// <c>validate_frame_content_size</c>: a frame's own header must declare the size its metadata
    /// promised. Cheap, and it catches the mismatch that would otherwise be a short decompression.
    /// </summary>
    private static void RequireDeclaredContentSize(
        ReadOnlySpan<byte> frame, ulong declared, int index)
    {
        if (!ZstandardDecoder.TryGetMaxDecompressedLength(frame, out long content))
        {
            CompressedThrow.Format($"{Id} frame {index} does not declare a content size.");
        }

        if ((ulong)content != declared)
        {
            CompressedThrow.Format(
                $"{Id} frame {index} metadata declares {declared} uncompressed bytes, but its " +
                $"header declares {content}.");
        }
    }

    /// <summary>Counts the rows that hold a value; that is how many the frames actually store.</summary>
    private static int CountValid(ArrayDecodeContext context, Validity validity, int length)
    {
        ValidityMask mask = ValidityMask.From(context, validity);
        if (mask.AllValid)
        {
            return length;
        }

        if (mask.AllInvalid)
        {
            return 0;
        }

        // A popcount per word rather than a call per row. The two uniform kinds returned above
        // leave only a bitmap here, so the bits are exactly what the kernel counts.
        return BitmapKernels.CountSet(mask.Bits, mask.BitOffset, length);
    }

    private static long CheckedSize(ulong value, int index, string what)
    {
        if (value > int.MaxValue)
        {
            CompressedThrow.Format($"{Id} frame {index} declares a {what} of {value}, past this reader's limit.");
        }

        return (long)value;
    }
}
