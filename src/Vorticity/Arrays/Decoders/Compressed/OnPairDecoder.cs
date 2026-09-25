using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.onpair</c> into a canonical varbin view. A code is a <c>u16</c> naming one of
/// up to 65536 dictionary tokens of at most sixteen bytes, and there is no escape code because a
/// conformant dictionary holds all 256 single-byte tokens; decoding a row is therefore a plain
/// concatenation of the tokens its codes name, with the row boundaries living on the decoded side.
/// </summary>
/// <remarks>
/// The children are <c>[dict_offsets, codes, codes_offsets, uncompressed_lengths, validity?]</c>,
/// and their lengths come from the metadata rather than from the wire, because slot children do
/// not persist their own.
/// </remarks>
internal sealed class OnPairDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.onpair";

    /// <summary>The longest dictionary token, in bytes.</summary>
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

    /// <summary>A code is a <c>u16</c>, so the dictionary holds at most 2^16 tokens.</summary>
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
    public override bool SelectsWithoutFullDecode => true;

    // No pushed-down comparison here: this encoding is read and never written, so the only files
    // carrying it are bare arrays from the reference implementation, with no named column a
    // predicate could reach.

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
        return node.BufferCount == 1 && node.ChildCount >= 4 &&
            context.ChildDecodesRange(in node, 1) && context.ChildDecodesRange(in node, 2) &&
            context.ChildDecodesRange(in node, 3) && context.ValidityDecodesRange(in node, 4);
    }

    /// <summary>
    /// Decodes a range of rows from its own slice of the code stream: the range's code offsets
    /// bound the slice, and its lengths size its heap, as the whole node's do for the whole stream.
    /// </summary>
    /// <remarks>
    /// The dictionary is every range's, and is decoded and checked whole for each: it is bounded by
    /// 65536 tokens of sixteen bytes, where the rows a range saves decoding are not bounded at all.
    /// </remarks>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start, count);
    }

    /// <summary>
    /// Decodes only the wanted rows, by concatenating only their codes.
    /// </summary>
    /// <remarks>
    /// Structurally identical to <c>FsstDecoder.DecodeSelected</c>, because the two encodings are
    /// twins at read time. `codes_offsets[i]..[i+1]` bounds row i's codes exactly, so a row is
    /// addressable without walking the rows before it, even though the reference implementation
    /// does not decode that way.
    ///
    /// `codes_offsets` is not itself pushed down: row i needs offsets i and i+1, so its wanted set
    /// is the union of `wanted` and `wanted + 1`, which costs more to build than the one integer
    /// decode it would save. `uncompressed_lengths` is pushed down, because one per wanted row is
    /// all the views need.
    ///
    /// The dictionary offset table is built once and shared across the wanted rows rather than per
    /// row. It is also the part of this decode that a take does not shrink: it is sized by the
    /// dictionary, not by the selection.
    /// </remarks>
    /// <inheritdoc/>
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
        CanonicalSupport.RequireBinaryLike(dtype, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 4, 5, Id);

        OnPairMetadata metadata = OnPairMetadata.Read(node.Metadata);
        bool ranged = !selective && (start != 0 || count != length);

        // Child 0: the dictionary's offsets, dict_size + 1 of them.
        int tokenCount = CheckedTokenCount(metadata.DictionarySize);
        CanonicalNode dictOffsets = DecodePart(
            context, in node, 0, Id + " dict_offsets", metadata.DictionaryOffsetsPType, tokenCount + 1);

        VortexBuffer dictionary = node.GetBuffer(0);
        int dictionaryBytes = ValidateDictionary(
            dictOffsets, metadata.DictionaryOffsetsPType, tokenCount, dictionary);

        // Child 1: the code stream, whose length the metadata carries. A range decodes only the
        // slice its code offsets bound, once it has them.
        int codesLength = ArrayDecodeContext.CheckedLength(metadata.CodesLength, Id + " codes_len");
        CanonicalNode codes = ranged
            ? default
            : DecodePart(context, in node, 1, Id + " codes", metadata.CodesPType, codesLength);

        // Child 2: the per-row code boundaries, len + 1 of them. Only the first and last are read:
        // a whole-array decode walks the codes in order and never needs the interior. A range needs
        // its own and the one after its last row.
        int offsetCount = ArrayDecodeContext.CheckedLength((ulong)length + 1, Id + " codes_offsets");
        CanonicalNode codesOffsets = ranged
            ? DecodePartRange(
                context, in node, 2, Id + " codes_offsets", metadata.CodesOffsetsPType, offsetCount, start, count + 1)
            : DecodePart(context, in node, 2, Id + " codes_offsets", metadata.CodesOffsetsPType, offsetCount);

        int produced = selective ? wanted.Length : count;

        // Child 3: the decoded length of each row, zero for a null one. Not pushed down: selecting
        // it costs a canonical node and its buffer per split, which outweighs the integer decoding
        // it saves and leaves a scattered take allocating more rather than less.
        CanonicalNode uncompressedLengths = ranged
            ? DecodePartRange(
                context, in node, 3, Id + " uncompressed_lengths", metadata.UncompressedLengthsPType, length, start, count)
            : DecodePart(context, in node, 3, Id + " uncompressed_lengths", metadata.UncompressedLengthsPType, length);

        Validity validity = ranged
            ? context.DecodeValidityRange(in node, 4, dtype.Nullability, length, start, count)
            : context.DecodeValidity(in node, 4, dtype.Nullability, length);
        if (selective)
        {
            validity = Compute.CanonicalFilter.FilterValidity(context.Canonical, validity, wanted);
        }

        int total = TotalDecodedLength(
            uncompressedLengths, metadata.UncompressedLengthsPType, count, wanted, selective,
            out int longestRow);

        // A heap nobody points into is not worth renting, and on a take that is the common case: a
        // value of 12 bytes or fewer lives inside its own view, so a selection of short rows leaves
        // the heap unread. Renting it anyway takes a block from the pool's smallest size class,
        // which retains only a few, so a batch of many splits allocates a fresh owner object for
        // nearly every one of them.
        // The bound on the longest row is what makes the stack safe, not the bound on the total:
        // a view over a value longer than MaxInlineViewLength holds a pointer into the heap, and a
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
            // Uninitialized: both paths below write exactly `total` bytes and refuse the stream
            // if they do not, so no byte of the heap survives the allocator.
            heap = CanonicalSupport.AllocateUninitialized(context, total, 1, out destination);
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
                codesOffsets, metadata.CodesOffsetsPType, count, codesLength);
            if (ranged)
            {
                codes = DecodePartRange(
                    context, in node, 1, Id + " codes", metadata.CodesPType, codesLength, codeStart, codeEnd - codeStart);
                (codeStart, codeEnd) = (0, codeEnd - codeStart);
            }

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

        // Uninitialized: ViewKernels writes all sixteen bytes of every view. A null row stores a
        // zero length, so it consumes nothing of the heap and gets an empty view that is written
        // out rather than inherited from the allocator.
        VortexBuffer views = CanonicalSupport.AllocateUninitialized(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        // The UTF-8 sweep runs over the dictionary and not over the heap: this encoding has no
        // escape, so every byte of the decoded heap is copied from the dictionary blob verbatim,
        // "the dictionary is ASCII" and "the heap is ASCII" are the same statement, and the blob
        // is bounded while the heap is not. Anything with a high byte in it falls through to the
        // per-heap sweep.
        //
        // The blob is bounded by its last offset and not by its length: `ValidateDictionary` has
        // already established that bound, and the bytes past it are the read padding the reference
        // implementation requires, which no token ever emits.
        bool heapIsAscii = dtype.Kind == DTypeKind.Utf8
            && Ascii.IsValid(dictionary.Span[..dictionaryBytes]);

        bool referenced = ViewKernels.BuildFromLengths(
            uncompressedLengths.Values.Span, metadata.UncompressedLengthsPType,
            selective ? wanted : default, destination, writable, produced,
            dtype.Kind == DTypeKind.Utf8 && !heapIsAscii);

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
    /// Each row concatenates into the remaining heap rather than a slice of exactly its own length.
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
        long[] rented = ArrayPool<long>.Shared.Rent(tokenCount);
        try
        {
            Span<long> tokens = rented.AsSpan(0, tokenCount);
            BuildTokenTable(dictOffsets, offsetsPType, tokenCount, tokens, dictionary.Length);

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
                    codes, codesPType, (int)start, (int)end, tokens, dictionary,
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
            ArrayPool<long>.Shared.Return(rented);
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
        // The token offsets are read once into an int table rather than twice per code through
        // CanonicalSupport.ReadInteger's physical-type switch. There are at most 65536 tokens and
        // usually far more codes than that, so this trades a bounded pass for two switches and two
        // bounds checks on every code. ValidateDictionary has already proved every offset lies
        // inside the blob and never decreases, so the table needs no checking of its own.
        long[] rented = ArrayPool<long>.Shared.Rent(tokenCount);
        try
        {
            Span<long> tokens = rented.AsSpan(0, tokenCount);
            BuildTokenTable(dictOffsets, offsetsPType, tokenCount, tokens, dictionary.Length);
            return Concatenate(
                codes, codesPType, codeStart, codeEnd, tokens, dictionary, destination);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Packs each token's start and size into one <see cref="long"/>: <c>start | size &lt;&lt; 32</c>.
    /// </summary>
    /// <remarks>
    /// Unpacked, describing one token costs <c>offsets[code]</c> and <c>offsets[code + 1]</c> --
    /// two bounds-checked loads on every code of the stream. Packed it is one load and one shift,
    /// and there are at most 65 536 tokens against usually far more codes.
    /// <see cref="ValidateDictionary"/> has already proved every offset lies inside the blob, never
    /// decreases and spans at most <see cref="MaxTokenSize"/>, so the table needs no checking of
    /// its own and every size fits comfortably in the high half.
    /// </remarks>
    internal static void BuildTokenTable(
        ReadOnlySpan<byte> dictOffsets, PType offsetsPType, int tokenCount, Span<long> tokens, int dictionaryLength)
    {
        // A token whose sixteen bytes from its start would run past the blob carries NearEnd, so
        // the concatenation finds such a token among many with one or rather than a compare each.
        int wideStart = dictionaryLength - MaxTokenSize;
        int previous = (int)CanonicalSupport.ReadInteger(dictOffsets, offsetsPType, 0);
        for (int t = 0; t < tokenCount; t++)
        {
            int next = (int)CanonicalSupport.ReadInteger(dictOffsets, offsetsPType, t + 1);
            tokens[t] = (uint)previous | (previous > wideStart ? NearEnd : 0) | ((long)(next - previous) << 32);
            previous = next;
        }
    }

    /// <summary>The concatenation itself, with the code width resolved once.</summary>
    /// <remarks>
    /// Internal rather than private so that the kernel benchmarks can call it in the same process.
    /// It has no callers outside this file.
    /// </remarks>
    internal static int Concatenate(
        ReadOnlySpan<byte> codes,
        PType codesPType,
        int codeStart,
        int codeEnd,
        ReadOnlySpan<long> tokens,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination) => codesPType switch
        {
            PType.U8 => ConcatenateCore<byte>(codes, codeStart, codeEnd, tokens, dictionary, destination),
            PType.U16 => ConcatenateCore<ushort>(codes, codeStart, codeEnd, tokens, dictionary, destination),
            PType.U32 => ConcatenateCore<uint>(codes, codeStart, codeEnd, tokens, dictionary, destination),
            _ => ConcatenateCore<ulong>(codes, codeStart, codeEnd, tokens, dictionary, destination),
        };

    /// <summary>
    /// The concatenation with the code's physical type resolved by the JIT rather than by a switch
    /// per code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolving the code width once per instantiation is what makes the inner loop cheap: moving
    /// one token otherwise costs a switch on the code's physical type, a compare against the token
    /// count, two bounds-checked loads from the offsets table, a compare of <c>written + size</c>
    /// against the destination length, and a compare of <c>start</c> against the blob's wide-store
    /// limit.
    /// </para>
    /// <para>
    /// The destination bound disappears rather than moving, and only because the
    /// wide path already implies it: <c>written &lt;= wideLimit</c> is
    /// <c>written + MaxTokenSize &lt;= destination.Length</c>, and a token is at most
    /// <see cref="MaxTokenSize"/> bytes, so <c>written + size</c> is inside by construction. The
    /// exact-copy path keeps the check, because there it is load-bearing.
    /// </para>
    /// </remarks>
    private static int ConcatenateCore<TCode>(
        ReadOnlySpan<byte> codes,
        int codeStart,
        int codeEnd,
        ReadOnlySpan<long> tokens,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination)
        where TCode : unmanaged
    {
        if (destination.Length >= 2 * StageBytes)
        {
            return Staged<TCode>(codes, codeStart, codeEnd, tokens, dictionary, destination);
        }

        return Direct<TCode>(codes, codeStart, codeEnd, tokens, dictionary, destination);
    }

    /// <summary>Bytes of output gathered in a first-level-cache buffer before they go out.</summary>
    private const int StageBytes = 8 * 1024;

    /// <summary>A token whose sixteen bytes from its start would run past the blob: bit 31 of its start.</summary>
    private const long NearEnd = 1L << 31;

    /// <summary>
    /// The concatenation of a large output: the tokens go into a buffer of
    /// <see cref="StageBytes"/> that stays in the first-level cache, eight at a time, and each full
    /// buffer goes out in one copy.
    /// </summary>
    /// <remarks>
    /// A token is written as sixteen bytes and the next overwrites the slack, so every line of the
    /// output takes several stores that overlap. Into memory the cache does not hold, each line
    /// is fetched before those stores can land, and the stores queue behind it: a scan window's
    /// output took three times as long a code as a node's that fits the first level. Gathered in
    /// a buffer that stays there, they land at once, and the copy out writes whole lines.
    /// </remarks>
    private static int Staged<TCode>(
        ReadOnlySpan<byte> codes,
        int codeStart,
        int codeEnd,
        ReadOnlySpan<long> tokens,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> typed = MemoryMarshal.Cast<byte, TCode>(codes);
        Span<byte> stage = stackalloc byte[StageBytes + (Block * MaxTokenSize)];
        ref byte stageRef = ref MemoryMarshal.GetReference(stage);
        ref TCode codeRef = ref MemoryMarshal.GetReference(typed);
        ref long tokenRef = ref MemoryMarshal.GetReference(tokens);
        ref byte source = ref MemoryMarshal.GetReference(dictionary);
        int wideStart = dictionary.Length - MaxTokenSize;
        int written = 0;
        nint i = codeStart;
        nint filled = 0;
        while (i < codeEnd)
        {
            i = Blocks(ref codeRef, i, codeEnd, ref tokenRef, (uint)tokens.Length, ref source, ref stageRef, StageBytes, ref filled);
            if (i < codeEnd && filled < StageBytes)
            {
                // A block the fast loop would not take: a code past the dictionary, which raises
                // here, or a token too near the blob's end, copied exactly.
                uint code = WidenToken(typed[(int)i]);
                if (code >= (uint)tokens.Length)
                {
                    CompressedThrow.Format(
                        $"{Id} code {code} names a token the {tokens.Length}-entry dictionary does " +
                        "not hold.");
                }

                long packed = tokens[(int)code];
                int start = (int)(packed & (NearEnd - 1));
                int size = (int)(packed >> 32);
                if (start <= wideStart)
                {
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)start)).StoreUnsafe(ref Unsafe.Add(ref stageRef, filled));
                }
                else
                {
                    dictionary.Slice(start, size).CopyTo(stage.Slice((int)filled, size));
                }

                filled += size;
                i++;
            }

            if (filled >= StageBytes || i >= codeEnd)
            {
                if ((uint)written + (uint)filled > (uint)destination.Length)
                {
                    CompressedThrow.Format(
                        $"{Id}: the code stream decodes to more than the {destination.Length} bytes " +
                        "its uncompressed lengths account for.");
                }

                stage[..(int)filled].CopyTo(destination[written..]);
                written += (int)filled;
                filled = 0;
            }
        }

        return written;
    }

    /// <summary>Codes a step of <see cref="Blocks{TCode}"/>.</summary>
    private const int Block = 8;

    /// <summary>
    /// Eight codes at a time into <paramref name="stage"/> while their tokens all lie in the
    /// dictionary, none too near the blob's end, and the stage has not reached
    /// <paramref name="limit"/>; returns where it stopped.
    /// </summary>
    /// <remarks>
    /// The codes are checked against the dictionary by one vector compare and the tokens' ends by
    /// one or of their packed words, so a code costs its load, its token's load, its sixteen-byte
    /// copy and its share of a tree of sums of the sizes before it, three additions deep. Calls
    /// nothing, so that its state stays in registers.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Blocks<TCode>(
        ref TCode codes, nint i, nint end, ref long tokens, uint tokenCount, ref byte source, ref byte stage, nint limit,
        ref nint filled)
        where TCode : unmanaged
    {
        nint at = filled;
        while (i <= end - Block && at < limit)
        {
            ref TCode block = ref Unsafe.Add(ref codes, i);
            if (!AllTokens(ref block, tokenCount))
            {
                break;
            }

            long p0 = Unsafe.Add(ref tokens, (nint)WidenToken(block));
            long p1 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 1)));
            long p2 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 2)));
            long p3 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 3)));
            long p4 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 4)));
            long p5 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 5)));
            long p6 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 6)));
            long p7 = Unsafe.Add(ref tokens, (nint)WidenToken(Unsafe.Add(ref block, 7)));
            if (((((p0 | p1) | (p2 | p3)) | ((p4 | p5) | (p6 | p7))) & NearEnd) != 0)
            {
                break;
            }

            nint w0 = (nint)(p0 >> 32);
            nint w2 = (nint)(p2 >> 32);
            nint w4 = (nint)(p4 >> 32);
            nint w6 = (nint)(p6 >> 32);
            nint w01 = w0 + (nint)(p1 >> 32);
            nint w23 = w2 + (nint)(p3 >> 32);
            nint w45 = w4 + (nint)(p5 >> 32);
            nint w67 = w6 + (nint)(p7 >> 32);
            nint w03 = w01 + w23;
            nint w05 = w03 + w45;

            ref byte into = ref Unsafe.Add(ref stage, at);
            Copy(ref source, p0, ref into);
            Copy(ref source, p1, ref Unsafe.Add(ref into, w0));
            Copy(ref source, p2, ref Unsafe.Add(ref into, w01));
            Copy(ref source, p3, ref Unsafe.Add(ref into, w01 + w2));
            Copy(ref source, p4, ref Unsafe.Add(ref into, w03));
            Copy(ref source, p5, ref Unsafe.Add(ref into, w03 + w4));
            Copy(ref source, p6, ref Unsafe.Add(ref into, w05));
            Copy(ref source, p7, ref Unsafe.Add(ref into, w05 + w6));
            at += w03 + (w45 + w67);
            i += Block;
        }

        filled = at;
        return i;

        static void Copy(ref byte source, long packed, ref byte into) =>
            Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (nint)(uint)packed)).StoreUnsafe(ref into);
    }

    /// <summary>Whether each of eight codes names a token of a dictionary of <paramref name="tokenCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllTokens<TCode>(ref TCode block, uint tokenCount)
        where TCode : unmanaged
    {
        if (typeof(TCode) == typeof(ushort))
        {
            return tokenCount > ushort.MaxValue ||
                !Vector128.GreaterThanOrEqualAny(
                    Vector128.LoadUnsafe(ref Unsafe.As<TCode, ushort>(ref block)), Vector128.Create((ushort)tokenCount));
        }

        if (typeof(TCode) == typeof(byte))
        {
            return tokenCount > byte.MaxValue ||
                !Vector64.GreaterThanOrEqualAny(
                    Vector64.LoadUnsafe(ref Unsafe.As<TCode, byte>(ref block)), Vector64.Create((byte)tokenCount));
        }

        bool inside = true;
        for (int k = 0; k < Block; k++)
        {
            inside &= WidenToken(Unsafe.Add(ref block, k)) < tokenCount;
        }

        return inside;
    }

    /// <summary>The concatenation of an output small enough to be written where it goes.</summary>
    private static int Direct<TCode>(
        ReadOnlySpan<byte> codes,
        int codeStart,
        int codeEnd,
        ReadOnlySpan<long> tokens,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> typed = MemoryMarshal.Cast<byte, TCode>(codes);
        int written = 0;

        // Past these points the wide store is unsafe and the exact copy takes over: the output
        // lacks sixteen writable bytes behind it, or the token lacks sixteen readable ones ahead
        // of it.
        int wideLimit = destination.Length - MaxTokenSize;
        int wideStart = dictionary.Length - MaxTokenSize;
        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte source = ref MemoryMarshal.GetReference(dictionary);

        // Four at a time, and the reason is the dependency chain rather than the instruction count.
        // One token per iteration serializes: the store's address needs `written`, `written` needs
        // this token's size, and the size needs a load from the table indexed by a code that was
        // itself just loaded. That is a load-to-add-to-store chain of five or six cycles that
        // nothing can overlap. Four independent table loads, then a three-add prefix sum over
        // their sizes, then four stores to addresses all known at once, costs about the same chain
        // for four tokens as the serial form costs for one.
        //
        // The block runs only when it needs no test of its own: `written <= blockLimit` is
        // `written + 4 * MaxTokenSize <= destination.Length`, so all four wide stores are inside
        // by construction, and the four starts are checked against the blob's limit with a max
        // rather than four branches.
        int blockLimit = destination.Length - (4 * MaxTokenSize);
        int tokenLimit = tokens.Length;
        int i = codeStart;
        while (true)
        {
            if (i + 4 > codeEnd || written > blockLimit)
            {
                if (i >= codeEnd)
                {
                    break;
                }

                i = One(typed, i, tokens, dictionary, destination, ref output, ref source,
                    wideLimit, wideStart, ref written);
                continue;
            }

            uint a = WidenToken(typed[i]);
            uint b = WidenToken(typed[i + 1]);
            uint c = WidenToken(typed[i + 2]);
            uint d = WidenToken(typed[i + 3]);
            if (a >= (uint)tokenLimit || b >= (uint)tokenLimit ||
                c >= (uint)tokenLimit || d >= (uint)tokenLimit)
            {
                // One of the four is out of range: the single-token step re-reads them in order
                // and raises on the offending one, with its index.
                i = One(typed, i, tokens, dictionary, destination, ref output, ref source,
                    wideLimit, wideStart, ref written);
                continue;
            }

            long pa = tokens[(int)a];
            long pb = tokens[(int)b];
            long pc = tokens[(int)c];
            long pd = tokens[(int)d];

            int sa = (int)pa;
            int sb = (int)pb;
            int sc = (int)pc;
            int sd = (int)pd;
            if (((pa | pb | pc | pd) & NearEnd) != 0)
            {
                // A token too near the end of the blob for a 16-byte read. Step one and retry the
                // block: a `break` here would give up on the wide path for the whole rest of the
                // stream because of one token at the end of the dictionary.
                i = One(typed, i, tokens, dictionary, destination, ref output, ref source,
                    wideLimit, wideStart, ref written);
                continue;
            }

            int oa = written;
            int ob = oa + (int)(pa >> 32);
            int oc = ob + (int)(pb >> 32);
            int od = oc + (int)(pc >> 32);
            written = od + (int)(pd >> 32);

            Vector128.StoreUnsafe(
                Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sa)),
                ref Unsafe.Add(ref output, (uint)oa));
            Vector128.StoreUnsafe(
                Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sb)),
                ref Unsafe.Add(ref output, (uint)ob));
            Vector128.StoreUnsafe(
                Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sc)),
                ref Unsafe.Add(ref output, (uint)oc));
            Vector128.StoreUnsafe(
                Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sd)),
                ref Unsafe.Add(ref output, (uint)od));
            i += 4;
        }

        return written;
    }

    /// <summary>One token: the path the block falls back to, and the only one that can raise.</summary>
    /// <returns>The next code index.</returns>
    private static int One<TCode>(
        ReadOnlySpan<TCode> typed,
        int i,
        ReadOnlySpan<long> tokens,
        ReadOnlySpan<byte> dictionary,
        Span<byte> destination,
        ref byte output,
        ref byte source,
        int wideLimit,
        int wideStart,
        ref int written)
        where TCode : unmanaged
    {
        uint code = WidenToken(typed[i]);
        if (code >= (uint)tokens.Length)
        {
            CompressedThrow.Format(
                $"{Id} code {code} names a token the {tokens.Length}-entry dictionary does " +
                "not hold.");
        }

        long packed = tokens[(int)code];
        int start = (int)(packed & (NearEnd - 1));
        int size = (int)(packed >> 32);

        if (written <= wideLimit && start <= wideStart)
        {
            // One 16-byte store per token, then advance by the token's real length: legal because
            // the bytes past the token are garbage the next store overwrites.
            Vector128.StoreUnsafe(
                Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)start)),
                ref Unsafe.Add(ref output, (uint)written));
            written += size;
            return i + 1;
        }

        if (written + size > destination.Length)
        {
            CompressedThrow.Format(
                $"{Id}: the code stream decodes to more than the {destination.Length} bytes " +
                "its uncompressed lengths account for.");
        }

        dictionary.Slice(start, size).CopyTo(destination.Slice(written, size));
        written += size;
        return i + 1;
    }

    /// <summary>Widens one code, saturating anything past a token index so the check refuses it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint WidenToken<TCode>(TCode code)
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

        ulong wide = Unsafe.As<TCode, ulong>(ref code);
        return wide > uint.MaxValue ? uint.MaxValue : (uint)wide;
    }

    /// <summary>
    /// <c>validate_safety</c>: the offsets must start at zero, be strictly increasing (no empty
    /// token), describe tokens of at most <see cref="MaxTokenSize"/> bytes, and end inside the blob.
    /// </summary>
    /// <returns>
    /// The dictionary's last offset, which bounds the live bytes of the blob: everything past it is
    /// read padding that no token can emit. Returned rather than read again by
    /// the caller because this walk has it in hand, and a second
    /// <see cref="CanonicalSupport.ReadInteger"/> would be a second per-node dispatch for a value
    /// already computed.
    /// </returns>
    private static int ValidateDictionary(
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

        // The logical end, not the reference implementation's read-padded one. That decoder copies
        // a fixed sixteen bytes per token and so needs padding past the last token's start; this
        // one copies the real length and needs only the last offset to fall inside the blob, which
        // accepts a padded file too and refuses only files the reference would also refuse.
        if (previous > dictionary.Length)
        {
            CompressedThrow.Format(
                $"{Id}'s dictionary offsets end at {previous}, past its {dictionary.Length}-byte blob.");
        }

        return (int)previous;
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
        // Typed once rather than per row, because otherwise every iteration goes through
        // `ReadInteger`'s switch on the physical type just to add one number, and this loop runs
        // over every row of the scan. The overflow cap is checked at the end instead of per row --
        // a sum of at most 2^31 values each below 2^63 cannot wrap a `long`, so the running total
        // is exact until it is tested.
        ReadOnlySpan<byte> raw = lengths.Values.Span;
        int count = selective ? wanted.Length : length;
        (long total, long longest, int negative) = ViewKernels.SumLengths(
            raw, ptype, selective ? wanted : default, count);

        if (negative >= 0)
        {
            CompressedThrow.Format(
                $"{Id} row {(selective ? wanted[negative] : negative)} declares a negative " +
                "uncompressed length.");
        }

        if (total > int.MaxValue)
        {
            CompressedThrow.Format($"{Id} uncompressed lengths sum past {int.MaxValue} bytes.");
        }

        longestRow = (int)Math.Min(longest, int.MaxValue);
        return (int)total;
    }


    /// <summary>Decodes one non-nullable integer child and checks its shape.</summary>
    /// <param name="context">The decode.</param>
    /// <param name="node">The array.</param>
    /// <param name="childIndex">The child's position.</param>
    /// <param name="name">The child, as a message names it: a constant, so that naming it costs nothing until it is needed.</param>
    /// <param name="ptype">The child's integer type.</param>
    /// <param name="length">The child's rows.</param>
    private static CanonicalNode DecodePart(
        ArrayDecodeContext context,
        in ArrayNode node,
        int childIndex,
        string name,
        PType ptype,
        int length)
    {
        CanonicalSupport.RequireIntegerPType(ptype, name);
        DType childType = context.Types.Primitive(ptype, Nullability.NonNullable);
        int index = context.DecodeChild(in node, childIndex, childType, length);
        return CanonicalSupport.RequirePrimitiveChild(context, index, ptype, length, name);
    }

    /// <summary>
    /// Decodes <paramref name="count"/> values of one non-nullable integer child from
    /// <paramref name="start"/>, as <see cref="DecodePart"/> decodes them all.
    /// </summary>
    private static CanonicalNode DecodePartRange(
        ArrayDecodeContext context,
        in ArrayNode node,
        int childIndex,
        string name,
        PType ptype,
        int length,
        int start,
        int count)
    {
        CanonicalSupport.RequireIntegerPType(ptype, name);
        DType childType = context.Types.Primitive(ptype, Nullability.NonNullable);
        int index = context.DecodeChildRange(in node, childIndex, childType, length, start, count);
        return CanonicalSupport.RequirePrimitiveChild(context, index, ptype, count, name);
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
