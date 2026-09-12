// Concatenation of canonical nodes. `vortex.chunked` is the only decoder that needs it
// (vortex-array-0.86.1/src/arrays/chunked/vtable/mod.rs, then the encoding's `execute`, which
// canonicalizes by concatenating), and the constant builder reuses it to tile a fixed-size-list
// row.
//
// Every chunk shares the parent's dtype, so every chunk canonicalizes to the same CanonicalKind and
// this file is a switch over the nine kinds rather than a general kernel. Buffers come from
// CanonicalArena.Allocate, which is the only writable memory a decoder may have (contract §8.4).
using System;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Concatenates canonical nodes that share a dtype into one canonical node.</summary>
internal static class CanonicalConcat
{
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
        for (int i = 1; i < chunks.Length; i++)
        {
            CanonicalKind other = arena.GetNode(chunks[i]).Kind;
            if (other != kind)
            {
                throw new VortexFormatException(
                    $"Chunk 0 canonicalizes to {kind} but chunk {i} to {other}; a chunked array's " +
                    "chunks all share its dtype and cannot disagree.");
            }
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

        return kind switch
        {
            CanonicalKind.Bool => ConcatBool(context, dtype, length, chunks, validity),
            CanonicalKind.Primitive => ConcatPrimitive(context, dtype, length, chunks, validity),
            CanonicalKind.Decimal => ConcatDecimal(context, dtype, length, chunks, validity),
            CanonicalKind.VarBinView => ConcatVarBinView(context, dtype, length, chunks, validity),
            CanonicalKind.ListView => ConcatListView(context, dtype, length, chunks, validity, depth),
            CanonicalKind.FixedSizeList => ConcatFixedSizeList(context, dtype, length, chunks, validity, depth),
            _ => ConcatStruct(context, dtype, length, chunks, validity, depth),
        };
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
        VortexBuffer values = CanonicalSupport.Allocate(context, totalBytes, Align, out Span<byte> writable);

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

        return arena.AddPrimitive(dtype, length, validity, ptype, values);
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
        VortexBuffer values = CanonicalSupport.Allocate(context, totalBytes, Align, out Span<byte> writable);

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

        return arena.AddDecimal(
            dtype, length, validity, storage, dtype.Precision, dtype.Scale, values);
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
        VortexBuffer views = CanonicalSupport.Allocate(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);

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
                ValidityMask mask = ValidityMask.From(context, chunk.Validity);
                ReadOnlySpan<byte> source = chunk.Views.Span;

                for (int j = 0; j < chunk.Length; j++, row++)
                {
                    if (!mask.IsValid(j))
                    {
                        // Null rows keep BinaryView::empty_view(); their stored view was never
                        // validated and must not be rebased.
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
                            $"Row {j} of chunk {i} references data buffer {index}; the chunk has " +
                            $"{chunk.DataBufferCount}.");
                    }

                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        target[8..12], (uint)(bufferBase + (int)index));
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
        DType elementType = dtype.ElementType;

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
        int fieldCount = dtype.FieldCount;

        Span<int> perChunkStack = stackalloc int[StackSmall];
        Span<int> fieldStack = stackalloc int[StackSmall];
        Scratch<int> perChunk = new Scratch<int>(chunks.Length, perChunkStack);
        Scratch<int> fields = new Scratch<int>(fieldCount, fieldStack);
        try
        {
            Span<int> sources = perChunk.Span;
            Span<int> results = fields.Span;

            for (int f = 0; f < fieldCount; f++)
            {
                for (int i = 0; i < chunks.Length; i++)
                {
                    CanonicalNode chunk = arena.GetNode(chunks[i]);
                    if (chunk.FieldCount != fieldCount)
                    {
                        throw new VortexFormatException(
                            $"Chunk {i} has {chunk.FieldCount} fields where the dtype declares {fieldCount}.");
                    }

                    sources[i] = chunk.GetFieldIndex(f);
                }

                results[f] = Concat(context, dtype.GetField(f), length, sources, depth + 1);
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
