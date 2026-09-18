// Concatenation of canonical nodes. `vortex.chunked` is the only decoder that needs it
// (vortex-array-0.86.1/src/arrays/chunked/vtable/mod.rs, then the encoding's `execute`, which
// canonicalizes by concatenating), and the constant builder reuses it to tile a fixed-size-list
// row.
//
// Every chunk shares the parent's dtype, so every chunk canonicalizes to the same CanonicalKind and
// this file is a switch over the nine kinds rather than a general kernel. Buffers come from
// CanonicalArena.Allocate, which is the only writable memory a decoder may have (contract §8.4).
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Concatenates canonical nodes that share a dtype into one canonical node.</summary>
internal static class CanonicalConcat
{
    /// <summary>Chunk indices held on the stack before renting; Z1b-c2b.</summary>
    private const int StackChunks = 32;

    private const int StackSmall = 32;

    /// <summary>Alignment every materialized buffer is given: the strictest we ever require.</summary>
    private const int Align = CanonicalSupport.MaxRequiredAlignment;

    /// <summary>
    /// Concatenates <paramref name="chunks"/>, in order, into one canonical node of
    /// <paramref name="dtype"/> and <paramref name="length"/> rows.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The dtype every chunk was decoded at, and the result carries.</param>
    /// <param name="length">Total row count; must equal the sum of the chunks' lengths.</param>
    /// <param name="chunks">Canonical node indices, in order.</param>
    /// <returns>The concatenated node's index.</returns>
    /// <exception cref="VortexFormatException">The chunks disagree in shape, or the lengths do not sum.</exception>
    internal static int Concat(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks) =>
        Concat(context, dtype, length, chunks, depth: 1);

    /// <summary>
    /// Concatenates <paramref name="repeat"/> copies of one node. Used only by the constant builder,
    /// for a fixed-size-list row whose elements must be laid out positionally.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The dtype of the node and of the result.</param>
    /// <param name="length">Total row count; must equal <c>repeat * node.Length</c>.</param>
    /// <param name="nodeIndex">The node to repeat.</param>
    /// <param name="repeat">How many copies, non-negative.</param>
    /// <returns>The repeated node's index.</returns>
    internal static int Repeat(
        ArrayDecodeContext context, DType dtype, int length, int nodeIndex, int repeat)
    {
        if (repeat == 1)
        {
            return nodeIndex;
        }

        Span<int> stack = stackalloc int[StackSmall];
        Scratch<int> scratch = new Scratch<int>(repeat, stack);
        try
        {
            Span<int> indices = scratch.Span;
            indices.Fill(nodeIndex);
            return Concat(context, dtype, length, indices, depth: 1);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int Concat(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Concat");

        CanonicalArena arena = context.Canonical;

        long total = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            total += arena.GetNode(chunks[i]).Length;
        }

        if (total != length)
        {
            throw new VortexFormatException(
                $"Chunk lengths sum to {total} but the array declares {length} rows.");
        }

        if (chunks.Length == 0)
        {
            return CanonicalFill.BuildZeroed(
                context, dtype, 0, Validity.FromNullability(dtype.Nullability));
        }

        if (chunks.Length == 1)
        {
            CanonicalNode only = arena.GetNode(chunks[0]);
            if (only.DType == dtype)
            {
                return chunks[0];
            }
        }

        CanonicalKind kind = arena.GetNode(chunks[0]).Kind;
        bool mixedConstants = false;
        for (int i = 1; i < chunks.Length; i++)
        {
            CanonicalKind other = arena.GetNode(chunks[i]).Kind;
            if (other == kind)
            {
                continue;
            }

            // A CONSTANT CHUNK BESIDE A CHUNK THAT IS NOT ONE is not the malformed file this check
            // is for. `distributions/repeated_prefix_utf8` is exactly it: the writer folded one
            // chunk to `vortex.constant` and left the others alone, so with the constant form on
            // the kinds disagree where the tiled form made them agree. Expanding the constants is
            // what restores the agreement, and it restores it to what the tiled form produced.
            if (other == CanonicalKind.Constant || kind == CanonicalKind.Constant)
            {
                mixedConstants = true;
                continue;
            }

            throw new VortexFormatException(
                $"Chunk 0 canonicalizes to {kind} but chunk {i} to {other}; a chunked array's " +
                "chunks all share its dtype and cannot disagree.");
        }

        if (mixedConstants)
        {
            return ConcatExpandingConstants(context, dtype, length, chunks, depth);
        }

        // Null carries no per-row validity and an Extension takes its storage's, so neither may
        // trigger the bitmap materialization ConcatValidity would otherwise do for nothing.
        if (kind == CanonicalKind.Null)
        {
            return arena.AddNull(dtype, length);
        }

        if (kind == CanonicalKind.Extension)
        {
            return ConcatExtension(context, dtype, length, chunks, depth);
        }

        Validity validity = ConcatValidity(context, dtype, length, chunks);

        // EXHAUSTIVE BY CONSTRUCTION (PERF-AUDIT-v2.md §2.4bis, Z1b-c1): the `_` arm this had was a
        // Struct arm in disguise, so a tenth kind would have been concatenated field by field over
        // fields it does not have. Every kind is NAMED now -- Null and Extension included, at the
        // throw the two early returns above make unreachable -- and IDE0072 (error, see
        // .editorconfig) fails the build when a named kind is missing.
        return kind switch
        {
            CanonicalKind.Bool => ConcatBool(context, dtype, length, chunks, validity),
            CanonicalKind.Primitive => ConcatPrimitive(context, dtype, length, chunks, validity),
            CanonicalKind.Decimal => ConcatDecimal(context, dtype, length, chunks, validity),
            CanonicalKind.VarBinView => ConcatVarBinView(context, dtype, length, chunks, validity),
            CanonicalKind.ListView => ConcatListView(context, dtype, length, chunks, validity, depth),
            CanonicalKind.FixedSizeList => ConcatFixedSizeList(context, dtype, length, chunks, validity, depth),
            CanonicalKind.Struct => ConcatStruct(context, dtype, length, chunks, validity, depth),
            CanonicalKind.Null or CanonicalKind.Extension => throw new UnreachableException(
                $"{kind} returns above, before ConcatValidity."),
            // CONVERTIT (Z1b-c2b) : deux constantes EGALES restent une constante, deux
            // differentes materialisent. C'est la seule regle du §2.4 qui ne se derive pas
            // de la forme mais des donnees, et elle decide a la concat plutot qu'au decode
            // parce que c'est la seule qui voit les chunks ensemble.
            CanonicalKind.Constant =>
                ConcatConstant(context, dtype, length, chunks, validity, depth),
            _ => throw new UnreachableException($"CanonicalKind {(byte)kind} is not defined."),
        };
    }


    /// <summary>
    /// Concatenates chunks of which SOME are constant: every constant is expanded, and the concat
    /// the shared kind already has runs over the result.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The chunked array's dtype.</param>
    /// <param name="length">Total row count across the chunks.</param>
    /// <param name="chunks">The chunk node indices, at least one of them Constant and one not.</param>
    /// <param name="depth">Recursion depth.</param>
    /// <returns>The concatenated node's index.</returns>
    /// <remarks>
    /// There is nothing to save here and the expansion is the honest answer: the column holds more
    /// than one value, so it cannot stay a constant, and the chunk that was one has to become what
    /// its neighbours already are. It re-enters <c>Concat</c> at the SAME depth, which terminates
    /// because the list it re-enters with holds no constant.
    /// </remarks>
    private static int ConcatExpandingConstants(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, int depth)
    {
        CanonicalArena arena = context.Canonical;
        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> expanded = scratch.Span;
            for (int i = 0; i < chunks.Length; i++)
            {
                expanded[i] = arena.GetNode(chunks[i]).Kind == CanonicalKind.Constant
                    ? arena.MaterializeConstant(chunks[i])
                    : chunks[i];
            }

            return Concat(context, dtype, length, expanded, depth);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>Concatenating constants: equal ones stay constant, different ones materialize.</summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The chunked array's dtype.</param>
    /// <param name="length">Total row count across the chunks.</param>
    /// <param name="chunks">The chunk node indices, all of kind Constant.</param>
    /// <param name="validity">The concatenated validity, already built by the caller.</param>
    /// <param name="depth">Recursion depth, for the materializing path.</param>
    /// <returns>The concatenated node's index.</returns>
    /// <remarks>
    /// PERF-AUDIT-v2.md §2.4's rule, and Z1b-c2b implements it. The comparison is over the ELEMENTS,
    /// which are one value each, so deciding costs a memcmp per chunk rather than a pass over the
    /// rows. When they differ there is nothing to be saved: the column really does hold more than
    /// one value, and the materialized form is the honest one.
    /// </remarks>
    private static int ConcatConstant(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks,
        Validity validity, int depth)
    {
        CanonicalArena arena = context.Canonical;
        ReadOnlySpan<byte> first = arena.GetNode(chunks[0]).ConstantElement;
        bool same = true;
        for (int i = 1; i < chunks.Length && same; i++)
        {
            same = first.SequenceEqual(arena.GetNode(chunks[i]).ConstantElement);
        }

        if (same)
        {
            return arena.AddConstant(dtype, length, validity, first);
        }

        // DIFFERENT VALUES: the column really does hold more than one, so the materialized form is
        // the honest one. Each chunk is expanded through the arena's memoized twin and handed to the
        // concat the underlying kind already has.
        //
        // This path was deferred at Z1b-c2b on the grounds that no corpus file reaches it. That was
        // WRONG, and the probe said so within a minute: `ChunkedConstantTests` builds exactly this,
        // and the §1.6 rule -- do not write a path no measurement reaches -- only licenses skipping
        // a path when you have LOOKED. Reading the corpus is not looking; running is.
        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> expanded = scratch.Span;
            for (int i = 0; i < chunks.Length; i++)
            {
                expanded[i] = arena.MaterializeConstant(chunks[i]);
            }

            // Three arms and not a switch over DTypeKind: the constant form covers the three dtypes
            // `MaterializeConstant` can expand, and naming the twenty it cannot would say the
            // opposite of the truth. The twin's KIND is what decides, and it is one of three.
            if (dtype.Kind == DTypeKind.Decimal)
            {
                return ConcatDecimal(context, dtype, length, expanded, validity);
            }

            if (dtype.Kind is DTypeKind.Utf8 or DTypeKind.Binary)
            {
                return ConcatVarBinView(context, dtype, length, expanded, validity);
            }

            return ConcatPrimitive(context, dtype, length, expanded, validity);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    // ------------------------------------------------------------------------------- validity

    private static Validity ConcatValidity(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks)
    {
        CanonicalArena arena = context.Canonical;

        bool allValid = true;
        bool allInvalid = true;
        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            if (chunk.Length == 0)
            {
                // An empty chunk says nothing about the whole; contributing its state would turn a
                // legal zero-row chunk (encodings/chunked_empty_chunks) into a spurious bitmap.
                continue;
            }

            Validity v = chunk.Validity;
            allValid &= v.IsAllValid;
            allInvalid &= v.Kind == ValidityKind.AllInvalid;
        }

        if (length == 0 || allValid)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        if (allInvalid)
        {
            return Validity.AllInvalid;
        }

        int byteCount = CanonicalSupport.BitmapByteCount(length);
        VortexBuffer bits = CanonicalSupport.Allocate(context, byteCount, Align, out Span<byte> writable);

        int position = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            int chunkLength = chunk.Length;
            Validity v = chunk.Validity;

            if (v.IsAllValid)
            {
                CanonicalSupport.SetBits(writable, position, chunkLength);
            }
            else if (v.Kind == ValidityKind.Bitmap)
            {
                CanonicalNode source = arena.GetNode(v.CanonicalNodeIndex);
                CanonicalSupport.CopyBits(
                    source.Bits.Span, source.BitOffset, writable, position, chunkLength);
            }

            position += chunkLength;
        }

        int node = context.Canonical.AddBool(
            context.Types.Bool(Nullability.NonNullable), length, Validity.NonNullable, bits, 0);
        return Validity.Bitmap(node);
    }

    // ------------------------------------------------------------------------------ flat kinds

    private static int ConcatBool(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, Validity validity)
    {
        CanonicalArena arena = context.Canonical;
        int byteCount = CanonicalSupport.BitmapByteCount(length);
        VortexBuffer bits = CanonicalSupport.Allocate(context, byteCount, Align, out Span<byte> writable);

        int position = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            CanonicalSupport.CopyBits(
                chunk.Bits.Span, chunk.BitOffset, writable, position, chunk.Length);
            position += chunk.Length;
        }

        return arena.AddBool(dtype, length, validity, bits, 0);
    }

    private static int ConcatPrimitive(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, Validity validity)
    {
        CanonicalArena arena = context.Canonical;
        PType ptype = arena.GetNode(chunks[0]).PType;
        int width = ptype.ByteWidth();
        int totalBytes = ArrayDecodeContext.CheckedMultiply(length, width, "concatenated values");

        // NOT COPIED AT ALL when the chunks already lie end to end, which is the common shape and
        // not a lucky one: chunk buffers come either from consecutive segments of the same mapped
        // file or from consecutive bump allocations in the same arena. Concatenating them then
        // means memcpying several megabytes onto a byte-identical image of themselves.
        if (SameKind(arena, chunks, ptype) &&
            TryBorrowRun(arena, chunks, width, totalBytes, out VortexBuffer borrowed))
        {
            return arena.AddPrimitive(dtype, length, validity, ptype, borrowed);
        }

        // UNINITIALIZED, because the chunks tile the output: each contributes its whole values
        // buffer, and the widths agree, so the copies below cover every byte. Zero-filling first
        // doubled the memory traffic of a concatenation that is already bandwidth-bound - 8 MB of
        // memset in front of 8 MB of copy, on the one encoding whose entire cost is this loop.
        //
        // "Cover every byte" is CHECKED rather than assumed: if the chunks come up short the tail
        // is cleared below, so a disagreement between a chunk's declared length and its buffer
        // cannot leak pool bytes into a column. See CanonicalArena.AllocateUninitialized.
        VortexBuffer values = CanonicalSupport.AllocateUninitialized(
            context, totalBytes, Align, out Span<byte> writable);

        int offset = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            if (chunk.PType != ptype)
            {
                throw new VortexFormatException(
                    $"Chunk {i} holds {chunk.PType.Name()} values where chunk 0 holds {ptype.Name()}.");
            }

            ReadOnlySpan<byte> source = chunk.Values.Span;
            source.CopyTo(writable[offset..]);
            offset += source.Length;
        }

        if (offset < totalBytes)
        {
            writable[offset..].Clear();
        }

        return arena.AddPrimitive(dtype, length, validity, ptype, values);
    }

    /// <summary>Whether every chunk holds <paramref name="ptype"/>, so the borrow may be tried.</summary>
    /// <remarks>
    /// A mismatch is a format error, but it is NOT raised here: the copying path raises it with the
    /// chunk index in the message, and declining is what keeps this a fast-path test.
    /// </remarks>
    private static bool SameKind(CanonicalArena arena, ReadOnlySpan<int> chunks, PType ptype)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            if (arena.GetNode(chunks[i]).PType != ptype)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The chunks' values as ONE buffer, without copying, when they are already adjacent and in
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test is reference adjacency, not an assumption: each chunk's values must start exactly
    /// where the previous one ended, and the run must add up to <paramref name="totalBytes"/>.
    /// Anything else - a repeated chunk, a reordered one, alignment padding between two segments,
    /// a chunk whose buffer is shorter than its declared length - fails the walk and takes the
    /// copy.
    /// </para>
    /// <para>
    /// PERF-AUDIT-v2.md R8a. Taken by WIDTH rather than by <c>PType</c>, since a decimal run has a
    /// storage width and no ptype and the walk never looked at anything else. The kind check each
    /// caller needs -- same physical type, same decimal storage -- stays with the caller, which is
    /// also where a disagreement is a FORMAT ERROR rather than a reason to decline.
    /// </para>
    /// <para>
    /// IT FIRES MORE OFTEN THAN IT LOOKS, AND FAILS FOR ONE REASON. Counted over the fifty files of
    /// a million: 1 672 borrows out of 2 093 attempts, and every one of the 421 failures is a
    /// column whose chunks carry a validity bitmap -- the counts match exactly, on three different
    /// corpora. Decoding such a chunk allocates its values AND its bits, so the next chunk's values
    /// no longer start where the previous one ended. That is R8e, and it is why a nullable chunked
    /// column never borrows.
    /// </para>
    /// <para>
    /// The borrowed bytes outlive the result for the same reason every other decoder's do: they
    /// belong to the mapping or the arena that produced the chunks, and the concatenation is a
    /// node in that same arena.
    /// </para>
    /// </remarks>
    private static bool TryBorrowRun(
        CanonicalArena arena, ReadOnlySpan<int> chunks, int width, int totalBytes,
        out VortexBuffer values)
    {
        values = VortexBuffer.Empty;

        ReadOnlySpan<byte> first = default;
        ReadOnlySpan<byte> previous = default;
        int alignmentExponent = 0;
        int covered = 0;
        bool started = false;

        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            VortexBuffer buffer = chunk.Values;
            ReadOnlySpan<byte> span = buffer.Span;

            // A zero-row chunk is legal (encodings/chunked_empty_chunks) and contributes nothing;
            // its buffer may be the null-based empty one, which has no address to be adjacent to.
            if (span.IsEmpty)
            {
                continue;
            }

            // The chunk must contribute EXACTLY its declared rows. A buffer longer than that would
            // still sum to `totalBytes` if a later one came up short, and the borrowed run would
            // then hold the wrong bytes at the right size.
            if (span.Length != chunk.Length * width)
            {
                return false;
            }

            if (!started)
            {
                first = span;
                alignmentExponent = buffer.AlignmentExponent;
                started = true;
            }
            else if (!Unsafe.AreSame(
                ref MemoryMarshal.GetReference(span),
                ref Unsafe.Add(ref MemoryMarshal.GetReference(previous), previous.Length)))
            {
                return false;
            }

            previous = span;
            covered += span.Length;
        }

        if (!started || covered != totalBytes)
        {
            return false;
        }

        values = VortexBuffer.FromPinned(
            MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetReference(first), totalBytes),
            alignmentExponent);
        return true;
    }

    private static int ConcatDecimal(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, Validity validity)
    {
        CanonicalArena arena = context.Canonical;

        // Chunks share a precision but not necessarily a storage width, so widen to the widest and
        // sign-extend the narrower ones. Two's complement makes that a fill plus a copy.
        DecimalStorageType storage = arena.GetNode(chunks[0]).Storage;
        int width = DecimalStorage.ByteWidth(storage);
        for (int i = 1; i < chunks.Length; i++)
        {
            DecimalStorageType other = arena.GetNode(chunks[i]).Storage;
            int otherWidth = DecimalStorage.ByteWidth(other);
            if (otherWidth > width)
            {
                storage = other;
                width = otherWidth;
            }
        }

        int totalBytes = ArrayDecodeContext.CheckedMultiply(length, width, "concatenated decimals");

        // PERF-AUDIT-v2.md R8a. THE WIDENING PATH BELOW NEVER RUNS on anything measured: across the
        // 856-file corpus and the fifty files of a million, every chunk of every decimal column
        // already carried the widest storage, so the loop was `CopyTo` and nothing else. Which
        // makes this exactly `ConcatPrimitive`'s case and it gets the same two answers.
        //
        // FIRST, THE BORROW. Chunks decoded into one arena usually land end to end, and then the
        // concatenation is a memcpy of several megabytes onto a byte-identical image of itself.
        // Measured by short-circuiting the copy on `chunked_decimal` at a million rows: the loop is
        // **75 % of that scan** (447 us against 110).
        if (NoWideningNeeded(arena, chunks, width) &&
            TryBorrowRun(arena, chunks, width, totalBytes, out VortexBuffer contiguous))
        {
            return arena.AddDecimal(
                dtype, length, validity, storage, dtype.Precision, dtype.Scale, contiguous);
        }

        // SECOND, NO ZERO-FILL. The chunks tile the output when no widening is needed, and when one
        // is, the fill below writes the sign bytes of every row it touches. Either way the memset
        // is paid for nothing -- and the tail is cleared if the chunks come up short, exactly as
        // `ConcatPrimitive` does, so a length disagreement cannot leak pool bytes into a column.
        VortexBuffer values = CanonicalSupport.AllocateUninitialized(
            context, totalBytes, Align, out Span<byte> writable);

        int offset = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            int chunkWidth = DecimalStorage.ByteWidth(chunk.Storage);
            ReadOnlySpan<byte> source = chunk.Values.Span;

            if (chunkWidth == width)
            {
                source.CopyTo(writable[offset..]);
                offset += source.Length;
                continue;
            }

            for (int row = 0; row < chunk.Length; row++)
            {
                ReadOnlySpan<byte> element = source.Slice(row * chunkWidth, chunkWidth);
                Span<byte> target = writable.Slice(offset, width);
                target.Fill((element[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00);
                element.CopyTo(target);
                offset += width;
            }
        }

        // The guard that pays for `AllocateUninitialized`: if the chunks' buffers came up short of
        // their declared rows, the rest is zeroed rather than left as the pool found it.
        if (offset < totalBytes)
        {
            writable[offset..].Clear();
        }

        return arena.AddDecimal(
            dtype, length, validity, storage, dtype.Precision, dtype.Scale, values);
    }

    /// <summary>Whether every chunk already stores at <paramref name="width"/> bytes a row.</summary>
    /// <remarks>
    /// The borrow needs the bytes to be usable as they lie; a narrower chunk has to be sign-extended
    /// into place and cannot be. Across everything measured this is true of every decimal column
    /// there is, which is why the widening loop below has never actually run.
    /// </remarks>
    private static bool NoWideningNeeded(CanonicalArena arena, ReadOnlySpan<int> chunks, int width)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            if (DecimalStorage.ByteWidth(arena.GetNode(chunks[i]).Storage) != width)
            {
                return false;
            }
        }

        return true;
    }

    private static int ConcatVarBinView(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, Validity validity)
    {
        CanonicalArena arena = context.Canonical;

        long buffered = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            buffered += arena.GetNode(chunks[i]).DataBufferCount;
        }

        // In 64 bits then narrowed: an int accumulator could wrap negative on a pathological tree
        // and hand a negative count to the scratch allocator.
        int totalBuffers = ArrayDecodeContext.CheckedLength((ulong)buffered, "concatenated data buffers");

        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, "concatenated views");

        // PERF-AUDIT-v2.md R8b. A NULL ROW IS THE ONLY REASON THIS BUFFER NEEDS ZEROING: it keeps
        // `BinaryView::empty_view()` and nothing writes it. When every chunk is all-valid the views
        // tile the output and the memset is 16 MB paid for nothing at a million rows -- the same
        // argument `ConcatPrimitive` makes, and `TilesFully` checks it rather than assuming it.
        bool tiled = TilesFully(arena, chunks, length);
        VortexBuffer views = tiled
            ? CanonicalSupport.AllocateUninitialized(
                context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable)
            : CanonicalSupport.Allocate(
                context, viewBytes, CanonicalSupport.ViewSize, out writable);

        Span<VortexBuffer> stack = stackalloc VortexBuffer[StackSmall];
        Scratch<VortexBuffer> scratch = new Scratch<VortexBuffer>(totalBuffers, stack);
        try
        {
            Span<VortexBuffer> buffers = scratch.Span;
            int bufferBase = 0;
            int row = 0;

            for (int i = 0; i < chunks.Length; i++)
            {
                CanonicalNode chunk = arena.GetNode(chunks[i]);
                ReadOnlySpan<byte> source = chunk.Views.Span;

                // THE ALL-VALID CHUNK IS THE SHAPE, not a lucky case: 112 chunks of 112 on
                // `chunked_varbinview` at a million rows, and a chunked VarBinView written by the
                // reference is all-valid whenever its column is. It moves its views in ONE copy and
                // then walks them to rebase, instead of slicing twice and copying sixteen bytes a
                // row through the validity mask.
                if (chunk.Validity.IsAllValid)
                {
                    int chunkBytes = chunk.Length * CanonicalSupport.ViewSize;
                    Span<byte> block = writable.Slice(row * CanonicalSupport.ViewSize, chunkBytes);
                    source[..chunkBytes].CopyTo(block);
                    Rebase(block, chunk.Length, bufferBase, chunk.DataBufferCount, i);
                    row += chunk.Length;
                }
                else
                {
                    ConcatViewsMasked(
                        context, chunk, source, writable, ref row, bufferBase, i);
                }

                for (int b = 0; b < chunk.DataBufferCount; b++)
                {
                    buffers[bufferBase + b] = chunk.GetDataBuffer(b);
                }

                bufferBase += chunk.DataBufferCount;
            }

            return arena.AddVarBinView(dtype, length, validity, views, buffers);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Whether every chunk is all-valid and their rows sum to <paramref name="length"/>, so that
    /// every output byte is written and the buffer need not be zeroed first.
    /// </summary>
    private static bool TilesFully(CanonicalArena arena, ReadOnlySpan<int> chunks, int length)
    {
        long covered = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            CanonicalNode chunk = arena.GetNode(chunks[i]);
            if (!chunk.Validity.IsAllValid)
            {
                return false;
            }

            // A chunk whose buffer is shorter than its declared rows would leave a hole; the copy
            // below would throw rather than write it, but this is the test that keeps
            // `AllocateUninitialized`'s promise a checked one.
            if (chunk.Views.Span.Length < chunk.Length * CanonicalSupport.ViewSize)
            {
                return false;
            }

            covered += chunk.Length;
        }

        return covered == length;
    }

    /// <summary>
    /// Rewrites each buffered view's data-buffer index from chunk-local to concatenated, in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A view is four <c>u32</c>s -- size, prefix, buffer index, offset -- so the walk reads words
    /// 0 and 2 and writes word 2, with no slicing at all. The cast is the same one
    /// <c>ViewKernels</c> makes of on-wire little-endian data.
    /// </para>
    /// <para>
    /// THE BOUND IS CHECKED ON EVERY BUFFERED VIEW and the metadata never licenses skipping it
    /// (Class I). The inline views -- 65 % of them on `chunked_varbinview` -- leave after one load.
    /// </para>
    /// </remarks>
    private static void Rebase(
        Span<byte> block, int rows, int bufferBase, int dataBufferCount, int chunkIndex)
    {
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(block);
        uint limit = (uint)dataBufferCount;

        for (int j = 0; j < rows; j++)
        {
            int w = j * 4;
            if (words[w] <= CanonicalSupport.MaxInlineViewLength)
            {
                continue;
            }

            uint index = words[w + 2];
            if (index >= limit)
            {
                throw new VortexFormatException(
                    $"Row {j} of chunk {chunkIndex} references data buffer {index}; the chunk has " +
                    $"{dataBufferCount}.");
            }

            words[w + 2] = (uint)(bufferBase + (int)index);
        }
    }

    /// <summary>The row-at-a-time path, for a chunk that has null rows to skip over.</summary>
    private static void ConcatViewsMasked(
        ArrayDecodeContext context,
        in CanonicalNode chunk,
        ReadOnlySpan<byte> source,
        Span<byte> writable,
        ref int row,
        int bufferBase,
        int chunkIndex)
    {
        ValidityMask mask = ValidityMask.From(context, chunk.Validity);

        for (int j = 0; j < chunk.Length; j++, row++)
        {
            if (!mask.IsValid(j))
            {
                // Null rows keep BinaryView::empty_view(); their stored view was never validated
                // and must not be rebased. This is also the one thing that stops the caller from
                // using `AllocateUninitialized`: the zeroes ARE the empty view.
                continue;
            }

            ReadOnlySpan<byte> view =
                source.Slice(j * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            Span<byte> target =
                writable.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            view.CopyTo(target);

            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);
            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                continue;
            }

            uint index =
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            if (index >= (uint)chunk.DataBufferCount)
            {
                throw new VortexFormatException(
                    $"Row {j} of chunk {chunkIndex} references data buffer {index}; the chunk has " +
                    $"{chunk.DataBufferCount}.");
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                target[8..12], (uint)(bufferBase + (int)index));
        }
    }

    // --------------------------------------------------------------------------- nested kinds

    private static int ConcatListView(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        ReadOnlySpan<int> chunks,
        Validity validity,
        int depth)
    {
        CanonicalArena arena = context.Canonical;

        // THE ELEMENT DTYPE COMES FROM THE CHILD, NOT FROM THE PARENT, and the difference is a
        // `vortex.map`: its canonical form is a ListView, but its DTYPE is Map, which has no
        // `ElementType` -- `MapDecoder` derives `Struct{key, value}` and puts it on the elements
        // child. Reading the parent worked for as long as nothing chunked or repartitioned a map
        // column, which nothing did until the writer started choosing its own chunk boundaries.
        DType elementType = chunks.Length == 0
            ? dtype.ElementType
            : arena.GetNode(arena.GetNode(chunks[0]).ElementsIndex).DType;

        Span<int> stack = stackalloc int[StackSmall];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> elements = scratch.Span;
            long totalElements = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                CanonicalNode chunk = arena.GetNode(chunks[i]);
                elements[i] = chunk.ElementsIndex;
                totalElements += arena.GetNode(chunk.ElementsIndex).Length;
            }

            int elementCount = ArrayDecodeContext.CheckedLength(
                (ulong)Math.Max(totalElements, 0), "concatenated list elements");
            int elementsIndex = Concat(context, elementType, elementCount, elements, depth + 1);

            // Offsets are rebased onto the concatenated elements, so the source widths no longer
            // matter; u64 is the only width guaranteed to hold every rebased offset.
            int offsetBytes = ArrayDecodeContext.CheckedMultiply(length, 8, "concatenated list offsets");
            VortexBuffer offsets = CanonicalSupport.Allocate(
                context, offsetBytes, Align, out Span<byte> offsetSpan);
            VortexBuffer sizes = CanonicalSupport.Allocate(
                context, offsetBytes, Align, out Span<byte> sizeSpan);

            long elementsBase = 0;
            int row = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                CanonicalNode chunk = arena.GetNode(chunks[i]);
                ReadOnlySpan<byte> chunkOffsets = chunk.Offsets.Span;
                ReadOnlySpan<byte> chunkSizes = chunk.Sizes.Span;
                PType offsetPType = chunk.OffsetPType;
                PType sizePType = chunk.SizePType;

                for (int j = 0; j < chunk.Length; j++, row++)
                {
                    long offset = CanonicalSupport.ReadInteger(chunkOffsets, offsetPType, j);
                    long size = CanonicalSupport.ReadInteger(chunkSizes, sizePType, j);
                    CanonicalSupport.WriteInteger(offsetSpan, PType.U64, row, offset + elementsBase);
                    CanonicalSupport.WriteInteger(sizeSpan, PType.U64, row, size);
                }

                elementsBase += arena.GetNode(chunk.ElementsIndex).Length;
            }

            return arena.AddListView(
                dtype, length, validity, elementsIndex, offsets, PType.U64, sizes, PType.U64);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int ConcatFixedSizeList(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        ReadOnlySpan<int> chunks,
        Validity validity,
        int depth)
    {
        CanonicalArena arena = context.Canonical;
        uint size = dtype.FixedSize;
        int elementCount = FixedSizeListDecoder.ElementCount(length, size);

        Span<int> stack = stackalloc int[StackSmall];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> elements = scratch.Span;
            for (int i = 0; i < chunks.Length; i++)
            {
                CanonicalNode chunk = arena.GetNode(chunks[i]);
                if (chunk.FixedSize != size)
                {
                    throw new VortexFormatException(
                        $"Chunk {i} holds {chunk.FixedSize} elements per row where the dtype says {size}.");
                }

                elements[i] = chunk.ElementsIndex;
            }

            int elementsIndex = Concat(context, dtype.ElementType, elementCount, elements, depth + 1);
            return arena.AddFixedSizeList(dtype, length, validity, elementsIndex, size);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int ConcatStruct(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        ReadOnlySpan<int> chunks,
        Validity validity,
        int depth)
    {
        CanonicalArena arena = context.Canonical;

        // A VARIANT DTYPE NAMES NO FIELDS, AND THIS METHOD USED TO ASK IT FOR THEM. A variant
        // column's canonical form is `Struct{metadata, value}` (VariantDecoder) while its SCHEMA
        // still says `variant`, whose own FieldCount is 0 -- so the loop below ran zero times, the
        // per-chunk agreement check that lives INSIDE it never ran, and `AddStruct` published a
        // struct with no children at all. Both children were dropped in silence, and the failure
        // surfaced one frame later in the writer ("a variant column is a two-field struct; this one
        // has 0 fields"): `variant` and `parquet_variant` could not be written back at all as soon
        // as a file held more than one pending chunk (v2 W-22a).
        //
        // Every other walk over a canonical struct in this library -- CanonicalSlice, CanonicalFilter,
        // MaskProjection, ProjectionTrim -- reads the count off the NODE. This one is the exception,
        // and it is the exception because it also needs each field's dtype, which for a struct only
        // the schema has. For a variant the children carry their own: `binary`, and a `binary` whose
        // nullability the file's metadata decided.
        bool variant = dtype.Kind == DTypeKind.Variant;
        int fieldCount = variant
            ? arena.GetNode(chunks[0]).FieldCount
            : dtype.FieldCount;

        Span<int> perChunkStack = stackalloc int[StackSmall];
        Span<int> fieldStack = stackalloc int[StackSmall];
        Scratch<int> perChunk = new Scratch<int>(chunks.Length, perChunkStack);
        Scratch<int> fields = new Scratch<int>(fieldCount, fieldStack);
        try
        {
            Span<int> sources = perChunk.Span;
            Span<int> results = fields.Span;

            // CHECKED BEFORE THE FIELD LOOP, NOT INSIDE IT. This is the same check as before and it
            // used to live one level down, where a `fieldCount` of 0 meant it never ran at all --
            // which is exactly how a variant's two children came to be dropped without a word. A
            // guard that is skipped whenever the count is wrong in the one direction that matters
            // is not a guard.
            for (int i = 0; i < chunks.Length; i++)
            {
                int actual = arena.GetNode(chunks[i]).FieldCount;
                if (actual != fieldCount)
                {
                    throw new VortexFormatException(
                        $"Chunk {i} has {actual} fields where the dtype declares {fieldCount}.");
                }
            }

            for (int f = 0; f < fieldCount; f++)
            {
                for (int i = 0; i < chunks.Length; i++)
                {
                    sources[i] = arena.GetNode(chunks[i]).GetFieldIndex(f);
                }

                // Read before the recursion: it appends records, and the arena may reallocate them.
                DType fieldType = variant ? arena.GetNode(sources[0]).DType : dtype.GetField(f);
                results[f] = Concat(context, fieldType, length, sources, depth + 1);
            }

            return arena.AddStruct(dtype, length, validity, results);
        }
        finally
        {
            fields.Dispose();
            perChunk.Dispose();
        }
    }

    private static int ConcatExtension(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, int depth)
    {
        CanonicalArena arena = context.Canonical;

        Span<int> stack = stackalloc int[StackSmall];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> storages = scratch.Span;
            for (int i = 0; i < chunks.Length; i++)
            {
                storages[i] = arena.GetNode(chunks[i]).StorageIndex;
            }

            int storage = Concat(context, dtype.StorageType, length, storages, depth + 1);
            return arena.AddExtension(dtype, length, storage);
        }
        finally
        {
            scratch.Dispose();
        }
    }
}
