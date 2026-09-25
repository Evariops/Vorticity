using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Concatenates canonical nodes that share a dtype into one canonical node.</summary>
/// <remarks>
/// Every chunk shares the parent's dtype, so every chunk canonicalizes to the same kind; that is
/// why this is a switch over the canonical kinds rather than a general kernel. The buffers it
/// writes come from the arena, which is the only writable memory a decoder may hold.
/// </remarks>
internal static class CanonicalConcat
{
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

        for (int i = 0; i < chunks.Length; i++)
        {
            if (arena.GetNode(chunks[i]).Kind is CanonicalKind.Dictionary or CanonicalKind.RunEnd)
            {
                return ConcatDecoding(context, dtype, length, chunks, depth);
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

            // A constant chunk beside a chunk that is not one is not the malformed file this check
            // is for: a writer may fold one chunk to `vortex.constant` and leave its neighbours
            // alone, which makes the kinds disagree without the dtypes disagreeing. Expanding the
            // constants restores the agreement.
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

        // Every kind is named, Null and Extension included, at a throw the two early returns above
        // make unreachable. A catch-all arm would quietly concatenate a new kind as whatever the
        // arm above it does; naming them all lets the exhaustiveness analyzer fail the build
        // instead.
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
            // Equal constants stay a constant, different ones materialize. This is the one choice
            // that follows from the data rather than from the shape, so it is made here rather
            // than at decode: concatenation is the only place that sees the chunks together.
            CanonicalKind.Constant =>
                ConcatConstant(context, dtype, length, chunks, validity, depth),
            CanonicalKind.Dictionary or CanonicalKind.RunEnd => throw new UnreachableException(
                $"{kind} chunks are decoded above, before ConcatValidity."),
            _ => throw new UnreachableException($"CanonicalKind {(byte)kind} is not defined."),
        };
    }

    /// <summary>
    /// Concatenates chunks of which some are dictionary or run-end nodes: each of those is decoded
    /// to its canonical twin, and the concat re-enters over the result.
    /// </summary>
    /// <remarks>
    /// Two chunks' codes index two dictionaries, and two chunks' ends two row spaces, so neither
    /// form survives a concatenation without a merge; decoding is the one answer that holds for
    /// every pair. The re-entry terminates because the list it re-enters with holds no encoded node.
    /// </remarks>
    private static int ConcatDecoding(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<int> chunks, int depth)
    {
        CanonicalArena arena = context.Canonical;
        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> decoded = scratch.Span;
            for (int i = 0; i < chunks.Length; i++)
            {
                decoded[i] = arena.Decoded(chunks[i]);
            }

            return Concat(context, dtype, length, decoded, depth);
        }
        finally
        {
            scratch.Dispose();
        }
    }


    /// <summary>
    /// Concatenates chunks of which only some are constant: every constant is expanded, and the
    /// concat the shared kind already has runs over the result.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The chunked array's dtype.</param>
    /// <param name="length">Total row count across the chunks.</param>
    /// <param name="chunks">The chunk node indices, at least one of them Constant and one not.</param>
    /// <param name="depth">Recursion depth.</param>
    /// <returns>The concatenated node's index.</returns>
    /// <remarks>
    /// There is nothing to save here: the column holds more than one value, so it cannot stay a
    /// constant, and the chunk that was one has to become what its neighbours already are. It
    /// re-enters <c>Concat</c> at the same depth, which terminates because the list it re-enters
    /// with holds no constant.
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
    /// The comparison is over the constant elements, which are one value each, so deciding costs a
    /// memcmp per chunk rather than a pass over the rows. When they differ there is nothing to be
    /// saved: the column really does hold more than one value.
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

        // Different values: the column holds more than one, so it has to materialize. Each chunk
        // is expanded through the arena's memoized twin and handed to the concat the underlying
        // kind already has.
        Span<int> stack = stackalloc int[StackChunks];
        Scratch<int> scratch = new Scratch<int>(chunks.Length, stack);
        try
        {
            Span<int> expanded = scratch.Span;
            for (int i = 0; i < chunks.Length; i++)
            {
                expanded[i] = arena.MaterializeConstant(chunks[i]);
            }

            // Three arms and not a switch over DTypeKind: the constant form covers only the three
            // dtypes `MaterializeConstant` can expand, and naming the ones it cannot would say the
            // opposite of the truth.
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
                // legal zero-row chunk into a spurious bitmap.
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

        // Nothing is copied when the chunks already lie end to end, which is the common shape and
        // not a lucky one: chunk buffers come either from consecutive segments of the same mapped
        // file or from consecutive bump allocations in the same arena. Copying them would mean
        // writing a byte-identical image of themselves.
        if (SameKind(arena, chunks, ptype) &&
            TryBorrowRun(arena, chunks, width, totalBytes, out VortexBuffer borrowed))
        {
            return arena.AddPrimitive(dtype, length, validity, ptype, borrowed);
        }

        // Uninitialized, because the chunks tile the output: each contributes its whole values
        // buffer and the widths agree, so the copies below cover every byte. Zero-filling would
        // double the memory traffic of a concatenation that is already bandwidth-bound.
        //
        // "Cover every byte" is checked rather than assumed: if the chunks come up short the tail
        // is cleared below, so a disagreement between a chunk's declared length and its buffer
        // cannot leak pool bytes into a column.
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
    /// A mismatch is a format error, but it is not raised here: the copying path raises it with the
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
    /// The chunks' values as a single buffer, without copying, when they are already adjacent and
    /// in order.
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
    /// It takes a width rather than a <c>PType</c>, because a decimal run has a storage width and
    /// no ptype and the walk looks at nothing else. The kind check each caller needs -- same
    /// physical type, same decimal storage -- stays with the caller, which is also where a
    /// disagreement is a format error rather than a reason to decline.
    /// </para>
    /// <para>
    /// A nullable chunked column never borrows: decoding such a chunk allocates its bits as well as
    /// its values, so the next chunk's values do not start where the previous one ended. That is
    /// the one reason the walk fails in practice.
    /// </para>
    /// <para>
    /// The borrowed bytes outlive the result for the same reason every other decoder's do: they
    /// belong to the mapping or the arena that produced the chunks, and the concatenation is a
    /// node in that same arena.
    /// </para>
    /// <para>
    /// The run it publishes may cross more than one chunk's buffer, and that is the point: since
    /// adjacency is tested by address, two blocks that happen to sit end to end are borrowed as a
    /// single span. Both of the things that usually make such a span wrong are absent here. The
    /// bytes are native - a raw pointer into a mapping or into a rented block, never a managed
    /// object - so crossing is pointer arithmetic over memory the collector neither tracks nor
    /// moves; and the blocks are released together with the batch that holds them, never one while
    /// another is still borrowed.
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

            // A zero-row chunk is legal and contributes nothing; its buffer may be the null-based
            // empty one, which has no address to be adjacent to.
            if (span.IsEmpty)
            {
                continue;
            }

            // The chunk must contribute exactly its declared rows. A buffer longer than that would
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

        // Decimal chunks in practice all carry the widest storage, so the widening path below is
        // the rare one and this is `ConcatPrimitive`'s case, with the same two answers.
        //
        // First, the borrow: chunks decoded into one arena usually land end to end, and copying
        // them would write a byte-identical image of themselves. The loop dominates the scan of a
        // chunked decimal column, so declining to run it at all is the whole saving.
        if (NoWideningNeeded(arena, chunks, width) &&
            TryBorrowRun(arena, chunks, width, totalBytes, out VortexBuffer contiguous))
        {
            return arena.AddDecimal(
                dtype, length, validity, storage, dtype.Precision, dtype.Scale, contiguous);
        }

        // Second, no zero-fill. The chunks tile the output when no widening is needed, and when one
        // is, the fill below writes the sign bytes of every row it touches. Either way the memset
        // would be paid for nothing -- and the tail is cleared if the chunks come up short, exactly
        // as `ConcatPrimitive` does, so a length disagreement cannot leak pool bytes into a column.
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
    /// into place and cannot be.
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

        // A null row is the only reason this buffer needs zeroing: it keeps the empty view and
        // nothing writes it. When every chunk is all-valid the views tile the output and the
        // memset is paid for nothing -- the same argument `ConcatPrimitive` makes, and `TilesFully`
        // checks it rather than assuming it.
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

                // The all-valid chunk is the usual shape, not a lucky case: a chunked view column
                // is all-valid whenever the column is. Such a chunk moves its views in one copy
                // and then walks them to rebase, instead of slicing twice and copying one view a
                // row through the validity mask.
                if (chunk.Validity.IsAllValid)
                {
                    int chunkBytes = chunk.Length * CanonicalSupport.ViewSize;
                    Span<byte> block = writable.Slice(row * CanonicalSupport.ViewSize, chunkBytes);
                    RebaseInto(source[..chunkBytes], block, chunk.Length, bufferBase, chunk.DataBufferCount, i);
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
    /// The buffer index is bounds-checked on every buffered view; metadata never licenses skipping
    /// it. An inline view leaves after one load, since it references no buffer at all.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Copies a chunk's views into the concatenated ones, each referencing view's buffer index
    /// moved past the buffers of the chunks before it.
    /// </summary>
    /// <remarks>
    /// On Arm, four views at a time in one pass: loaded with their words taken apart, lengths,
    /// prefixes, buffer indices and offsets, a referencing view is one whose length is past the
    /// inline limit, its index is moved by a mask of that, an index past the chunk's buffers is
    /// gathered by or, and the four go back together into place. A chunk with a bad index is
    /// walked again view by view to name it. Elsewhere the views are copied and then rebased.
    /// </remarks>
    internal static unsafe void RebaseInto(
        ReadOnlySpan<byte> source, Span<byte> block, int rows, int bufferBase, int dataBufferCount, int chunkIndex)
    {
        int j = 0;
        if (AdvSimd.Arm64.IsSupported)
        {
            fixed (byte* from = source)
            fixed (byte* into = block)
            {
                Vector128<uint> inline = Vector128.Create((uint)CanonicalSupport.MaxInlineViewLength);
                Vector128<uint> limit = Vector128.Create((uint)dataBufferCount);
                Vector128<uint> shift = Vector128.Create((uint)bufferBase);
                Vector128<uint> bad = Vector128<uint>.Zero;
                for (; j <= rows - 4; j += 4)
                {
                    (Vector128<uint> sizes, Vector128<uint> prefixes, Vector128<uint> indices, Vector128<uint> offsets) =
                        AdvSimd.Arm64.Load4xVector128AndUnzip((uint*)(from + (j * CanonicalSupport.ViewSize)));
                    Vector128<uint> referencing = Vector128.GreaterThan(sizes, inline);
                    bad |= referencing & Vector128.GreaterThanOrEqual(indices, limit);
                    AdvSimd.Arm64.StoreVectorAndZip(
                        (uint*)(into + (j * CanonicalSupport.ViewSize)),
                        (sizes, prefixes, indices + (shift & referencing), offsets));
                }

                if (bad != Vector128<uint>.Zero)
                {
                    j = 0;
                }
            }
        }

        source[(j * CanonicalSupport.ViewSize)..].CopyTo(block[(j * CanonicalSupport.ViewSize)..]);
        Rebase(block[(j * CanonicalSupport.ViewSize)..], rows - j, bufferBase, dataBufferCount, chunkIndex, j);
    }

    private static void Rebase(
        Span<byte> block, int rows, int bufferBase, int dataBufferCount, int chunkIndex, int firstRow)
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
                    $"Row {firstRow + j} of chunk {chunkIndex} references data buffer {index}; the chunk has " +
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
                // Null rows keep the empty view; their stored view was never validated and must
                // not be rebased. This is also the one thing that stops the caller from using
                // `AllocateUninitialized`: the zeroes are the empty view.
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

        // The element dtype comes from the child, not from the parent, and the difference is a
        // map: its canonical form is a ListView, but its dtype is Map, which has no element type
        // -- the map decoder derives `Struct{key, value}` and puts it on the elements child. Read
        // from the parent, a chunked map column would lose its element dtype.
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

            // Offsets are rebased onto the concatenated elements, so the source widths do not
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

        // A variant dtype names no fields, so it cannot be asked for them: a variant column's
        // canonical form is `Struct{metadata, value}` while its schema still says `variant`, whose
        // own field count is zero. Taking the count from the dtype there would publish a struct
        // with no children at all, in silence.
        //
        // Every other walk over a canonical struct in this library reads the count off the node.
        // This one is the exception because it also needs each field's dtype, which for a struct
        // only the schema has; for a variant the children carry their own.
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

            // Checked before the field loop, not inside it: inside, a field count of zero would
            // skip the check entirely, which is the one direction in which the count being wrong
            // does real damage.
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
