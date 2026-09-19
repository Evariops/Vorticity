// vortex.fsst - vortex-fsst-0.86.1/src/array.rs `deserialize` and src/canonical.rs
// `canonicalize_fsst`. The kernel itself is FsstSymbolTable.
//
// The layout is unusual in one respect that shapes the whole decode: the ROW BOUNDARIES ARE ON THE
// DECODED SIDE, not the compressed one. `codes_offsets` splits the code stream per row, but the
// decode does not use it for that -- it decompresses the entire stream in one pass and then cuts
// the result with `uncompressed_lengths`. Two consequences worth stating, because both are
// load-bearing:
//
//   * a row's decoded bytes are `lengths[i]`, and the lengths must account for the decoded heap
//     EXACTLY (upstream asserts it in build_views). So the lengths are not a hint: they are the
//     only thing that says where a value ends.
//   * `codes_offsets` is used only to find the code stream's own extent, offsets[0]..offsets[len],
//     which for an unsliced array is upstream's `codes.sliced_bytes()`.
//
// Shapes, from `deserialize`:
//     3 buffers [symbols, symbol_lengths, codes] -> children [uncompressed_lengths, codes_offsets, validity?]
//     2 buffers [symbols, symbol_lengths]        -> children [codes: vortex.varbin, uncompressed_lengths]
//
// Only the 3-buffer shape is implemented. The 2-buffer one is upstream's `deserialize_legacy`, and
// it requires the codes child to still BE a vortex.varbin -- offsets plus a byte heap -- while this
// library canonicalizes every child, and a canonical VarBinView inlines values of 12 bytes or fewer
// into the views, where a contiguous code stream no longer exists. Reconstructing one would mean
// reaching past the arena into another encoding's serialized form. No corpus file uses the shape
// (all 55 are `vortex.fsst/c2/b3` or `/c3/b3`), so it is refused by name rather than implemented
// untested -- recorded in docs/90-registry.md as the gap it is.
using System;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.fsst</c> into a canonical varbin view.</summary>
public sealed class FsstDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.fsst";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly FsstDecoder Instance = new FsstDecoder();

    private FsstDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.fsst"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Fsst;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Answers an equality by compressing the literal and comparing code for code, so the column is
    /// never decompressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Equal values compress to equal codes, and equal codes decompress to equal values, so code
    /// equality IS value equality. The first half is the invariant
    /// <c>FsstCompressInvariantTests</c> holds; the second is decompression being a function.
    /// </para>
    /// <para>
    /// ONLY EQUALITY AND ITS NEGATION. An ordering on codes is not an ordering on the bytes they
    /// stand for -- a symbol's code says nothing about where its bytes sort -- so anything else
    /// declines and the scan decodes as it always has.
    /// </para>
    /// <para>
    /// MOST ROWS ARE REJECTED BY THEIR OFFSETS ALONE: a row whose code slice is not the needle's
    /// length cannot equal it, and that is two integer reads against touching its bytes. Only the
    /// rows of the right length are compared, which on a selective equality is few.
    /// </para>
    /// <para>
    /// The uncompressed lengths are not read at all. They size the decoded heap, and there is no
    /// heap here.
    /// </para>
    /// </remarks>
    public override bool TryCompare(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        Expressions.ComparisonOp op, Expressions.FilterLiteral literal, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (op is not (Expressions.ComparisonOp.Equal or Expressions.ComparisonOp.NotEqual) ||
            literal.Kind != Expressions.FilterLiteralKind.Bytes ||
            node.BufferCount != 3)
        {
            return false;
        }

        CanonicalSupport.RequireBinaryLike(dtype, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, 3, Id);

        FsstMetadata metadata = FsstMetadata.Read(node.Metadata);
        FsstSymbolTable table = FsstSymbolTable.Create(
            node.GetBuffer(0).Span, node.GetBuffer(1).Span, Id);

        ReadOnlySpan<byte> needle = literal.BytesValue;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(
            FsstSymbolTable.MaxCompressedLength(needle.Length) + 1);
        try
        {
            if (!table.TryCompress(needle, rented, out int needleLength))
            {
                return false;
            }

            ReadOnlySpan<byte> wanted = rented.AsSpan(0, needleLength);

            PType offsetsPType = metadata.CodesOffsetsPType;
            CanonicalSupport.RequireIntegerPType(offsetsPType, Id + " codes_offsets");
            int offsetCount = ArrayDecodeContext.CheckedLength(
                (ulong)length + 1, Id + " codes_offsets");
            DType offsetsType = context.Types.Primitive(offsetsPType, Nullability.NonNullable);
            int offsetsIndex = context.DecodeChild(in node, 1, offsetsType, offsetCount);
            CanonicalNode codesOffsets = CanonicalSupport.RequirePrimitiveChild(
                context, offsetsIndex, offsetsPType, offsetCount, Id + " codes_offsets");

            VortexBuffer codes = node.GetBuffer(2);
            ReadOnlySpan<byte> stream = CodeStream(codesOffsets, offsetsPType, length, codes);
            ReadOnlySpan<byte> raw = codesOffsets.Values.Span;

            Validity validity = context.DecodeValidity(in node, 2, dtype.Nullability, length);
            ValidityReader rows = ValidityReader.Of(context.Canonical, validity);

            byte match = op == Expressions.ComparisonOp.Equal
                ? Compute.Trilean.True
                : Compute.Trilean.False;
            byte miss = op == Expressions.ComparisonOp.Equal
                ? Compute.Trilean.False
                : Compute.Trilean.True;

            // The physical type of the offsets is resolved ONCE, for the reason
            // `TotalDecodedLength` gives: reading one offset a row through a switch on the type was
            // 15 % of a scan, and a loop that reads two is no better.
            switch (offsetsPType)
            {
                case PType.U8: Match<byte>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.U16: Match<ushort>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.U32: Match<uint>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.U64: Match<ulong>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.I8: Match<sbyte>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.I16: Match<short>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.I32: Match<int>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                case PType.I64: Match<long>(raw, stream, wanted, in rows, length, match, miss, destination); break;
                default: return false;
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Decodes only the wanted rows, by decompressing only their codes.
    /// </summary>
    /// <remarks>
    /// THE REASON THIS IS POSSIBLE AT ALL is the child the reference does not decode with:
    /// `codes_offsets[i]..[i+1]` bounds row i's codes exactly, so a row's compressed bytes are
    /// addressable without walking the rows before it. The header above explains why the REFERENCE
    /// does not work that way - it decompresses the whole stream in one pass and cuts the result
    /// with `uncompressed_lengths`, which is the right choice for a full scan and the wrong one for
    /// a take. docs/90's take table classified this encoding from that decode strategy rather than
    /// from the format, and called it a variable-length encoding whose row n cannot be found
    /// without rows 0..n-1. The argument is sound in general and does not apply here.
    ///
    /// What is NOT pushed down is `codes_offsets` itself, deliberately: row i needs offsets i AND
    /// i+1, so the wanted set for that child is not `wanted` but its union with `wanted + 1`.
    /// Building that union costs more than it saves - the child is one integer decode of len+1
    /// values, while the cost this method exists to avoid is the FSST kernel over the whole split,
    /// the heap allocation that sizes with it, and a view built for every row in it.
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
        if (node.BufferCount == 2)
        {
            CompressedThrow.Format(
                $"{Id} in its two-buffer form stores its codes as a nested vortex.varbin array; " +
                "this build reads only the three-buffer form.");
        }

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 3, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, 3, Id);

        FsstMetadata metadata = FsstMetadata.Read(node.Metadata);

        // The padded decode tables are built once PER CALL and handed to whichever path runs.
        //
        // THAT IS NOT ONCE PER TAKE, AND THE COMMENT HERE USED TO IMPLY IT WAS. PERF-AUDIT-v2.md
        // R11, measured on `--throughput --take` at a million rows: 54 976 calls to `Prepare` for
        // 54 976 wanted rows -- **one per row**. The 64 rows of a take are spread over the file, so
        // each lands in its own split, and each split is one `DecodeSelected` call that rebuilds
        // 2.3 KiB of tables to decode a single row. Doubling the build costs **at least 4,5 %** of
        // that take.
        //
        // It is left as it is, and the number is why: the `fsst` take axis reads **0.23** -- four
        // times the reference -- so 4,5 % of it buys nothing anyone is waiting for. The fix is not
        // here either: it is one prepared table cached per node for the life of a scan, which is
        // `ScanContext` scratch and therefore R14. On a full scan the count is what the audit
        // expected -- 86 calls for 86 million rows.
        Span<byte> symbolScratch = stackalloc byte[FsstSymbolTable.SymbolScratchBytes];
        Span<byte> widthScratch = stackalloc byte[FsstSymbolTable.WidthScratchBytes];
        FsstDecodeTable table = FsstSymbolTable
            .Create(node.GetBuffer(0).Span, node.GetBuffer(1).Span, Id)
            .Prepare(symbolScratch, widthScratch);

        int produced = selective ? wanted.Length : length;

        // The lengths ARE pushed down: one per wanted row is all the views need, and the child is
        // free to specialize its own take.
        PType lengthsPType = metadata.UncompressedLengthsPType;
        CanonicalSupport.RequireIntegerPType(lengthsPType, Id + " uncompressed_lengths");
        DType lengthsType = context.Types.Primitive(lengthsPType, Nullability.NonNullable);
        int lengthsIndex = selective
            ? context.DecodeChildSelected(in node, 0, lengthsType, length, wanted)
            : context.DecodeChild(in node, 0, lengthsType, length);
        CanonicalNode uncompressedLengths = CanonicalSupport.RequirePrimitiveChild(
            context, lengthsIndex, lengthsPType, produced, Id + " uncompressed_lengths");

        // VarBin offsets are len + 1, and they bound the code stream rather than the decoded one.
        PType offsetsPType = metadata.CodesOffsetsPType;
        CanonicalSupport.RequireIntegerPType(offsetsPType, Id + " codes_offsets");
        int offsetCount = ArrayDecodeContext.CheckedLength((ulong)length + 1, Id + " codes_offsets");
        DType offsetsType = context.Types.Primitive(offsetsPType, Nullability.NonNullable);
        int offsetsIndex = context.DecodeChild(in node, 1, offsetsType, offsetCount);
        CanonicalNode codesOffsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, offsetsPType, offsetCount, Id + " codes_offsets");

        Validity validity = context.DecodeValidity(in node, 2, dtype.Nullability, length);
        if (selective)
        {
            validity = Compute.CanonicalFilter.FilterValidity(context.Canonical, validity, wanted);
        }

        VortexBuffer codes = node.GetBuffer(2);

        int total = TotalDecodedLength(uncompressedLengths, lengthsPType, produced);
        // Uninitialized: the decode writes exactly `total` bytes and the check below refuses the
        // stream if it does not, so no byte of the heap survives the allocator.
        VortexBuffer heap = CanonicalSupport.AllocateUninitialized(
            context, total, 1, out Span<byte> destination);

        uint escapeBits = 0;
        if (selective)
        {
            DecodeRows(table, codesOffsets, offsetsPType, length, codes, uncompressedLengths,
                lengthsPType, wanted, destination, ref escapeBits);
        }
        else
        {
            ReadOnlySpan<byte> stream = CodeStream(codesOffsets, offsetsPType, length, codes);
            int written = table.Decode(stream, destination, Id, ref escapeBits);
            if (written != total)
            {
                CompressedThrow.Format(
                    $"{Id} decoded {written} bytes; its uncompressed lengths sum to {total}.");
            }
        }

        // THE SWEEP IS SKIPPED ONLY WHEN THE HEAP IS PROVABLY ASCII, and the proof is two facts
        // about where its bytes came from rather than a look at the bytes: every byte is either one
        // of the first `width` bytes of some symbol -- all below 0x80, which `SymbolsAreAscii`
        // establishes over two kilobytes of table -- or a byte the escape path wrote, all of which
        // `escapeBits` has accumulated. ASCII is valid UTF-8, so there is nothing left to check.
        // Anything else falls through to the full `Utf8.IsValid` exactly as before: the guarantee
        // is the same one, bought at two kilobytes instead of fifty-two megabytes.
        bool heapIsAscii = table.SymbolsAreAscii && (escapeBits & 0x80) == 0;
        bool requireUtf8 = dtype.Kind == DTypeKind.Utf8 && !heapIsAscii;

        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            produced, CanonicalSupport.ViewSize, Id + " views");

        // Uninitialized: ViewKernels writes all sixteen bytes of every view, null rows included.
        // A null row stores a zero length upstream, so it consumes nothing of the heap and gets an
        // empty view -- which is now WRITTEN rather than inherited from the allocator.
        VortexBuffer views = CanonicalSupport.AllocateUninitialized(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        // No `wanted` indirection: the lengths child was decoded SELECTIVELY, so it already holds
        // exactly the produced rows in selection order.
        ViewKernels.BuildFromLengths(
            uncompressedLengths.Values.Span, lengthsPType, default, destination, writable, produced,
            requireUtf8);

        if (total == 0)
        {
            return context.Canonical.AddVarBinView(dtype, produced, validity, views, default);
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = heap;
        return context.Canonical.AddVarBinView(dtype, produced, validity, views, single);
    }

    /// <summary>
    /// Decompresses one wanted row at a time, each from its own slice of the code stream.
    /// </summary>
    /// <remarks>
    /// Each row is decoded into the REMAINING heap rather than into a slice of exactly its own
    /// length, and the difference is not cosmetic. The kernel's fast path is an 8-byte store per
    /// symbol that it may only take while 8 bytes of slack remain; handed a destination cut to the
    /// row's exact size, every symbol in every row would fall to the narrow tail path instead. The
    /// declared length is still enforced - the kernel's return value must equal it - so cutting the
    /// span adds nothing a check does not already give.
    /// </remarks>
    private static void DecodeRows(
        in FsstDecodeTable table,
        CanonicalNode offsets,
        PType offsetsPType,
        int length,
        VortexBuffer codes,
        CanonicalNode lengths,
        PType lengthsPType,
        ReadOnlySpan<int> wanted,
        Span<byte> destination,
        ref uint escapeBits)
    {
        ReadOnlySpan<byte> rawOffsets = offsets.Values.Span;
        ReadOnlySpan<byte> rawLengths = lengths.Values.Span;
        ReadOnlySpan<byte> stream = codes.Span;
        int written = 0;

        for (int k = 0; k < wanted.Length; k++)
        {
            int row = wanted[k];
            long start = CanonicalSupport.ReadInteger(rawOffsets, offsetsPType, row);
            long end = CanonicalSupport.ReadInteger(rawOffsets, offsetsPType, row + 1);
            if (start < 0 || end < start || end > stream.Length)
            {
                CompressedThrow.Format(
                    $"{Id} row {row} spans codes [{start}, {end}) of a {stream.Length}-byte " +
                    "codes buffer.");
            }

            int expected = (int)CanonicalSupport.ReadInteger(rawLengths, lengthsPType, k);
            int got = table.Decode(
                stream.Slice((int)start, (int)(end - start)), destination.Slice(written), Id,
                ref escapeBits);
            if (got != expected)
            {
                CompressedThrow.Format(
                    $"{Id} row {row} decoded to {got} bytes; it declares {expected}.");
            }

            written += got;
        }
    }

    /// <summary>
    /// The extent of the code stream, <c>offsets[0]..offsets[length]</c>, validated against the
    /// codes buffer.
    /// </summary>
    private static ReadOnlySpan<byte> CodeStream(
        CanonicalNode offsets, PType ptype, int length, VortexBuffer codes)
    {
        ReadOnlySpan<byte> raw = offsets.Values.Span;
        long start = CanonicalSupport.ReadInteger(raw, ptype, 0);
        long end = CanonicalSupport.ReadInteger(raw, ptype, length);

        if (start < 0 || end < start || end > codes.Length)
        {
            // Not the generic throw helper: a ref struct cannot be its type argument.
            CompressedThrow.Format(
                $"{Id} codes span [{start}, {end}) of a {codes.Length}-byte codes buffer.");
        }

        return codes.Span.Slice((int)start, (int)(end - start));
    }

    /// <summary>Marks the rows whose code slice is the needle's, with the offsets typed once.</summary>
    /// <typeparam name="T">The physical type of <c>codes_offsets</c>.</typeparam>
    /// <param name="raw">The offsets child's bytes.</param>
    /// <param name="stream">The code stream the offsets bound.</param>
    /// <param name="wanted">The needle's codes.</param>
    /// <param name="rows">The column's validity.</param>
    /// <param name="length">The row count.</param>
    /// <param name="match">The state a matching row takes.</param>
    /// <param name="miss">The state every other valid row takes.</param>
    /// <param name="destination">Receives one state per row.</param>
    private static void Match<T>(
        ReadOnlySpan<byte> raw, ReadOnlySpan<byte> stream, ReadOnlySpan<byte> wanted,
        in ValidityReader rows, int length, byte match, byte miss, Span<byte> destination)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        ReadOnlySpan<T> offsets = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, T>(raw)[..(length + 1)];
        long origin = long.CreateChecked(offsets[0]);
        long previous = origin;
        int needleLength = wanted.Length;
        for (int row = 0; row < length; row++)
        {
            long next = long.CreateChecked(offsets[row + 1]);
            if (next < previous || next - origin > stream.Length)
            {
                CompressedThrow.Format(
                    $"{Id} row {row} spans codes [{previous}, {next}) of a " +
                    $"{stream.Length}-byte stream.");
            }

            int start = (int)(previous - origin);
            int size = (int)(next - previous);
            previous = next;

            if (!rows.IsValid(row))
            {
                destination[row] = Compute.Trilean.Unknown;
                continue;
            }

            // The length alone rejects a row without touching its bytes, which is most of them.
            destination[row] = size == needleLength && stream.Slice(start, size).SequenceEqual(wanted)
                ? match
                : miss;
        }
    }

    /// <summary>The sum of the per-row uncompressed lengths, which is the decoded heap's size.</summary>
    /// <remarks>
    /// Summed in <see cref="long"/> and capped: the lengths are file-supplied, and a row count of
    /// 8192 with a declared length of <c>u64::MAX</c> each would otherwise overflow into a small
    /// allocation that the decode then overruns.
    /// </remarks>
    private static int TotalDecodedLength(CanonicalNode lengths, PType ptype, int length)
    {
        // Typed once rather than per row: this loop was 15% of a 1M-row fsst scan, going through
        // `ReadInteger`'s switch on the physical type to add one number. The overflow cap moves to
        // the end -- a sum of at most 2^31 values each below 2^63 cannot wrap a `long`, so the
        // running total is exact until it is tested.
        (long total, _, int negative) = ViewKernels.SumLengths(
            lengths.Values.Span, ptype, default, length);

        if (negative >= 0)
        {
            CompressedThrow.Format($"{Id} row {negative} declares a negative uncompressed length.");
        }

        if (total > int.MaxValue)
        {
            CompressedThrow.Format($"{Id} uncompressed lengths sum past {int.MaxValue} bytes.");
        }

        return (int)total;
    }
}
