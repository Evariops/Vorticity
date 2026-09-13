// vortex.zstd - vortex-zstd-0.86.1/src/array.rs (`deserialize`, `validate`, `decompress_slice`,
// `decompress`, `walk_views`).
//
// Zstd here is an ARRAY encoding, not segment compression: the values of a utf8/binary/primitive
// column are stored as one or more zstd frames, optionally sharing a trained dictionary. Frames
// exist so a slice can decompress only what it needs; a whole-node decode like ours needs all of
// them, which collapses upstream's frame-selection walk to "every frame until the values run out".
//
// TWO THINGS ABOUT THIS ENCODING ARE UNLIKE EVERY OTHER DECODER HERE.
//
// First, NULLS ARE NOT STORED. The frames hold only the valid values, back to back, so the
// decompressed stream has `true_count` values for `length` rows and the output has to be scattered
// back across the null slots. Upstream says it outright: "ZSTD is a compact block compressor,
// meaning that null values are not stored inline in the data frames."
//
// Second, for utf8/binary the decompressed stream is NOT an Arrow layout. It is a bare sequence of
// `[u32 little-endian length][bytes]` records with no offsets array, walked forward to rebuild the
// views (`walk_views`). The length prefixes stay in the data buffer and the views point past them.
//
// Upstream segments that buffer at MAX_BUFFER_LEN = i32::MAX so view offsets fit in a u32. Our
// buffers are int-length by construction and a single allocation can never exceed int.MaxValue
// bytes, so the second segment is unreachable here and the views always carry buffer index 0. That
// is an argument about our own bounds, not an assumption about the file, so it holds for any input.
using System;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.zstd</c> into a canonical primitive or varbin view.</summary>
public sealed class ZstdDecoder : ArrayDecoder
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

        // 0 or 1 child; the one child, when present, is the validity bitmap.
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 0, 1, Id);
        bool isPrimitive = dtype.Kind == DTypeKind.Primitive;
        if (!isPrimitive && dtype.Kind is not (DTypeKind.Utf8 or DTypeKind.Binary))
        {
            CompressedThrow.Format(
                $"{Id} stores primitive, utf8 or binary values; this node's dtype is {dtype}.");
        }

        int frameCount = ZstdMetadata.CountFrames(node.Metadata);
        ZstdFrameMetadata[] frames = new ZstdFrameMetadata[frameCount];
        ZstdMetadata metadata = ZstdMetadata.Read(node.Metadata, frames);

        // `validate`: the dictionary buffer is present exactly when the metadata declares one, and
        // the frame buffers match the frame metadata one for one.
        bool hasDictionary = metadata.DictionarySize != 0;
        int expectedBuffers = frameCount + (hasDictionary ? 1 : 0);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, expectedBuffers, Id);

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);
        int valueCount = CountValid(context, validity, length);
        int byteWidth = isPrimitive ? dtype.PType.ByteWidth() : 1;

        int total = PlanFrames(frames, dtype, isPrimitive, byteWidth, valueCount, out int usedFrames);
        VortexBuffer values = Decompress(
            context, in node, metadata, frames, usedFrames, hasDictionary, total, byteWidth);

        return isPrimitive
            ? Scatter(context, dtype, length, validity, values, valueCount, byteWidth)
            : BuildViews(context, dtype, length, validity, values, valueCount);
    }

    /// <summary>
    /// Decides how many leading frames carry the values and how many bytes they decompress to.
    /// </summary>
    /// <remarks>
    /// Upstream walks frames tracking a value cursor and stops once the cursor passes the last
    /// value the slice wants. Unsliced, that window is <c>[0, valueCount)</c>, so the walk keeps
    /// frames until their cumulative value count covers every valid row.
    /// </remarks>
    private static int PlanFrames(
        ReadOnlySpan<ZstdFrameMetadata> frames,
        DType dtype,
        bool isPrimitive,
        int byteWidth,
        int valueCount,
        out int usedFrames)
    {
        long total = 0;
        long covered = 0;
        usedFrames = 0;

        for (int i = 0; i < frames.Length && covered < valueCount; i++)
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

            total += uncompressed;
            if (total > int.MaxValue)
            {
                CompressedThrow.Format(
                    $"{Id} frames declare {total} uncompressed bytes, past this reader's limit.");
            }

            covered += frameValues;
            usedFrames = i + 1;
        }

        return (int)total;
    }

    /// <summary>
    /// Decompresses <paramref name="usedFrames"/> frames, in order, into one allocation.
    /// </summary>
    private static VortexBuffer Decompress(
        ArrayDecodeContext context,
        in ArrayNode node,
        ZstdMetadata metadata,
        ReadOnlySpan<ZstdFrameMetadata> frames,
        int usedFrames,
        bool hasDictionary,
        int total,
        int byteWidth)
    {
        // The cap applies here and not one line later: `total` is a file-supplied sum, and a 1 KiB
        // frame can claim to expand to 100 GiB (docs/08-semantics.md §6).
        VortexBuffer output = CanonicalSupport.Allocate(
            context, total, byteWidth, out Span<byte> destination);
        if (total == 0)
        {
            return output;
        }

        int firstFrame = hasDictionary ? 1 : 0;
        ZstandardDictionary? dictionary = null;
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
                // dictionary - which no default writer produces.
                dictionary = ZstandardDictionary.Create(raw.Span);
            }

            int written = 0;
            for (int i = 0; i < usedFrames; i++)
            {
                ReadOnlySpan<byte> frame = node.GetBuffer(firstFrame + i).Span;
                RequireDeclaredContentSize(frame, frames[i].UncompressedSize, i);

                // Bounded by what is left of the planned total, so a frame that expands further
                // than advertised is refused by the decoder rather than overrunning.
                Span<byte> region = destination[written..];
                bool ok = dictionary is null
                    ? ZstandardDecoder.TryDecompress(frame, region, out int produced)
                    : ZstandardDecoder.TryDecompress(frame, region, out produced, dictionary);
                if (!ok)
                {
                    CompressedThrow.Format(
                        $"{Id} frame {i} did not decompress into the {region.Length} bytes its " +
                        "metadata left for it.");
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
            // Already dense: the decompressed buffer IS the values buffer.
            return context.Canonical.AddPrimitive(
                dtype, length, validity, dtype.PType, values.Slice(0, required));
        }

        int span = ArrayDecodeContext.CheckedMultiply(length, byteWidth, Id + " rows");
        VortexBuffer output = CanonicalSupport.Allocate(
            context, span, byteWidth, out Span<byte> destination);
        ReadOnlySpan<byte> source = values.Span;

        int next = 0;
        for (int row = 0; row < length; row++)
        {
            if (!mask.IsValid(row))
            {
                continue;
            }

            source.Slice(next * byteWidth, byteWidth).CopyTo(destination.Slice(row * byteWidth, byteWidth));
            next++;
        }

        return context.Canonical.AddPrimitive(dtype, length, validity, dtype.PType, output);
    }

    /// <summary>
    /// Rebuilds Arrow views by walking the length-prefixed value stream, then scatters them over
    /// the null slots.
    /// </summary>
    private static int BuildViews(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        Validity validity,
        VortexBuffer values,
        int valueCount)
    {
        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, Id + " views");

        ValidityMask mask = ValidityMask.From(context, validity);

        // THE ZERO FILL STAYS, and it was measured rather than assumed. An all-valid array writes
        // every one of the sixteen bytes of every view, so `AllocateUninitialized` is provable
        // here -- and it is 1% SLOWER (535 against 541 scans per 4 s). The memset is not pure
        // overhead: it pulls the sixteen-megabyte view buffer into cache just ahead of the loop
        // that rewrites it, so dropping it trades a sequential fill for a cold miss per row.
        // A null row needs the zeros anyway: it keeps `empty_view()` and the loop skips it.
        VortexBuffer views = CanonicalSupport.Allocate(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        ReadOnlySpan<byte> heap = values.Span;

        // ONE PASS OVER THE HEAP INSTEAD OF A CALL PER ROW. Every byte below 0x80 is a complete,
        // valid UTF-8 sequence on its own, so if the whole decompressed stream is ASCII then so is
        // every value inside it, whatever the boundaries -- and no per-row validation can fail.
        // `Ascii.IsValid` is one intrinsified sweep at memory speed; the per-row
        // `Utf8.IsValid` was 19% of a zstd scan, because a validator called on twenty bytes at a
        // time never reaches its stride.
        //
        // The four-byte length prefixes sit inside the heap and are NOT text, but they are ASCII
        // whenever a value is shorter than 128 bytes (the low byte) with three zero bytes above
        // it, which is the shape of essentially every string column. When the sweep does find a
        // high byte -- a real non-ASCII value, or a value at least 128 bytes long -- the per-row
        // path below is exactly what it was.
        bool requireUtf8 = dtype.Kind == DTypeKind.Utf8 && !Ascii.IsValid(heap);

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

            // Buffer index 0: see the header note on why the second segment is unreachable. One
            // call for both shapes, and two register stores rather than a memset plus a Memmove
            // per row - the same change ViewKernels.Write documents.
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

        if (heap.Length == 0)
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

        // A CALL PER ROW became a popcount per eight bytes. `mask` is a Bitmap here -- the two
        // uniform kinds returned above -- so the bits are exactly what the kernel counts
        // (PERF-AUDIT §4.2).
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
