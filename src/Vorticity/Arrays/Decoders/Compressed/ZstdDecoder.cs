using System;
using System.Numerics;
using System.Runtime.InteropServices;
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

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        // 0 or 1 child; the one child, when present, is the validity bitmap.
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 0, 1, Id);
        bool isPrimitive = dtype.Kind == DTypeKind.Primitive;
        if (!isPrimitive && dtype.Kind is not (DTypeKind.Utf8 or DTypeKind.Binary))
        {
            CompressedThrow.Format(
                $"{Id} stores primitive, utf8 or binary values; this node's dtype is {dtype}.");
        }

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
        ZstdMetadata metadata = ZstdMetadata.Read(node.Metadata, scratch.Span);
        int frameCount = metadata.FrameCount;
        Span<ZstdFrameMetadata> frames = scratch.Span[..frameCount];

        // `validate`: the dictionary buffer is present exactly when the metadata declares one, and
        // the frame buffers match the frame metadata one for one.
        bool hasDictionary = metadata.DictionarySize != 0;
        int expectedBuffers = frameCount + (hasDictionary ? 1 : 0);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, expectedBuffers, Id);

        bool ranged = start != 0 || count != length;
        Validity validity = ranged
            ? context.DecodeValidityRange(in node, 0, dtype.Nullability, length, start, count)
            : context.DecodeValidity(in node, 0, dtype.Nullability, length);
        int valueCount = CountValid(context, validity, count);
        int firstValue = ranged ? ValuesBefore(context, in node, length, start) : 0;
        int byteWidth = isPrimitive ? dtype.PType.ByteWidth() : 1;

        int total = PlanFrames(
            frames, dtype, isPrimitive, byteWidth, firstValue, valueCount,
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

        return BitmapKernels.CountSet(bits.Bits.Span, bits.BitOffset, start);
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
        out int firstFrame,
        out int usedFrames,
        out int skipped)
    {
        long total = 0;
        long covered = 0;
        long end = (long)firstValue + valueCount;
        firstFrame = 0;
        usedFrames = 0;
        skipped = 0;

        for (int i = 0; i < frames.Length && covered < end; i++)
        {
            ZstdFrameMetadata frame = frames[i];
            long uncompressed = CheckedSize(frame.UncompressedSize, i, "uncompressed size");
            long frameValues;
            if (frame.ValueCount != 0)
            {
                frameValues = CheckedSize(frame.ValueCount, i, "value count");
            }
            else if (isPrimitive)
            {
                // Older primitive-only metadata omitted the count; a fixed width makes the byte
                // count an exact value count.
                frameValues = uncompressed / byteWidth;
            }
            else if (frames.Length == 1)
            {
                // The same fallback would read a byte count as a value count for variable-width
                // values. One frame holds everything, so that case is still recoverable.
                frameValues = valueCount;
            }
            else
            {
                return CompressedThrow.Format<int>(
                    $"{Id} frame {i} of a {dtype.Kind} array declares no value count, and the " +
                    "array has more than one frame, so its values cannot be attributed.");
            }

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
                dictionary = ZstandardDictionary.Create(raw.Span);
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
    /// nothing, since the destination is zeroed, and a mixed word walks its set bits. Asking the
    /// mask row by row is a call and a switch a row, and was most of a nullable decode.
    /// </remarks>
    private static void Expand<T>(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length)
        where T : unmanaged
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source);
        Span<T> rows = MemoryMarshal.Cast<byte, T>(destination);
        ReadOnlySpan<byte> bits = mask.Bits;
        int bitOffset = mask.BitOffset;
        int next = 0;
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

            while (word != 0)
            {
                rows[row + BitOperations.TrailingZeroCount(word)] = values[next++];
                word &= word - 1;
            }
        }
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
            // shape, and writes two registers rather than clearing and copying the view per row.
            Span<byte> view = writable.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            CanonicalSupport.WriteView(view, value, (int)size, bufferIndex: 0, offset: start);

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
