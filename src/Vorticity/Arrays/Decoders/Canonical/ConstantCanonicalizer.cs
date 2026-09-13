// Materializes a `vortex.constant` node: n rows of one scalar, in whatever canonical form the
// dtype takes. Upstream's `constant_canonicalize`
// (vortex-array-0.86.1/src/arrays/constant/compute/canonical.rs) does the same job.
//
// The one interesting case is a list. A ListView's offsets are arbitrary - that is the whole point
// of the encoding - so a constant list is n rows all pointing at ONE copy of the k elements:
// O(k) work rather than O(n * k). A fixed-size list has no offsets and is positional, so there the
// k elements really are tiled n times, through the same concat machinery `vortex.chunked` uses.
using System;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Builds the canonical form of a repeated scalar.</summary>
internal static class ConstantCanonicalizer
{
    private const int Align = CanonicalSupport.MaxRequiredAlignment;
    private const int StackSmall = 32;

    /// <summary>
    /// Builds <paramref name="length"/> rows of <paramref name="scalar"/> at
    /// <paramref name="dtype"/>.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The dtype the node must produce.</param>
    /// <param name="length">Row count.</param>
    /// <param name="scalar">The value, already interpreted against the dtype's storage.</param>
    /// <returns>The new canonical node's index.</returns>
    /// <exception cref="VortexFormatException">
    /// The scalar cannot describe the dtype, or the dtype is a Map, Union or Variant.
    /// </exception>
    internal static int Build(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar) =>
        Build(context, dtype, length, in scalar, depth: 1);

    private static int Build(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Constant");

        if (dtype.IsDefault)
        {
            throw new VortexFormatException("A constant array cannot be built without a dtype.");
        }

        CanonicalArena arena = context.Canonical;

        // An extension's scalar is interpreted against its storage dtype (Phase 1 contract §0a C3),
        // which is exactly what TypedScalarReader already unwrapped it to.
        if (dtype.Kind == DTypeKind.Extension)
        {
            int storage = Build(context, dtype.StorageType, length, in scalar, depth + 1);
            return arena.AddExtension(dtype, length, storage);
        }

        if (dtype.Kind == DTypeKind.Null)
        {
            return arena.AddNull(dtype, length);
        }

        if (scalar.IsNull)
        {
            if (dtype.Nullability != Nullability.Nullable)
            {
                throw new VortexFormatException(
                    $"A null constant needs a nullable dtype; the array is declared {dtype}.");
            }

            return CanonicalFill.BuildZeroed(context, dtype, length, Validity.AllInvalid);
        }

        Validity validity = Validity.FromNullability(dtype.Nullability);

        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
            {
                VortexBuffer bits = CanonicalSupport.Allocate(
                    context, CanonicalSupport.BitmapByteCount(length), Align, out Span<byte> writable);
                if (scalar.AsBool)
                {
                    BitmapKernels.SetRange(writable, 0, length);
                }

                return arena.AddBool(dtype, length, validity, bits, 0);
            }

            case DTypeKind.Primitive:
            {
                PType ptype = dtype.PType;
                int bytes = ArrayDecodeContext.CheckedMultiply(
                    length, ptype.ByteWidth(), "constant values");

                // Uninitialized: `WriteTo` tiles the whole span, so every byte is written and the
                // zero-fill was a second full pass over the buffer for nothing.
                VortexBuffer values = CanonicalSupport.AllocateUninitialized(
                    context, bytes, Align, out Span<byte> writable);
                scalar.WriteTo(writable, ptype);
                return arena.AddPrimitive(dtype, length, validity, ptype, values);
            }

            case DTypeKind.Decimal:
                return BuildDecimal(context, dtype, length, in scalar, validity);

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                return BuildBinary(context, dtype, length, in scalar, validity);

            case DTypeKind.Struct:
                return BuildStruct(context, dtype, length, in scalar, validity, depth);

            case DTypeKind.List:
                return BuildList(context, dtype, length, in scalar, validity, depth);

            case DTypeKind.FixedSizeList:
                return BuildFixedSizeList(context, dtype, length, in scalar, validity, depth);

            default:
                throw new VortexFormatException(
                    $"Phase 1 has no canonical form for a {dtype.Kind} dtype.");
        }
    }

    private static int BuildDecimal(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar, Validity validity)
    {
        // The width comes from the SCALAR, not from the precision. Upstream's
        // `constant_canonicalize` (vortex-array-0.86.1/src/arrays/constant/vtable/canonical.rs)
        // uses `match_each_decimal_value!(value, ..)`, so the DecimalArray's values_type is the
        // scalar's own DecimalValue variant - which `bytes_from_proto` derives from the serialized
        // bytes_value LENGTH; `smallest_decimal_value_type` appears only in the all-null arm, which
        // CanonicalFill already matches. Upstream does not tie an array's values_type to its
        // precision either (DecimalData's own doc: "a DecimalArray can be built that stores a set
        // of precision=2 values in a Buffer<i256>"), and every Arrow-sourced decimal column is
        // i128/i256 whatever its precision - so a chunk the compressor folded to vortex.constant
        // must report the same width as the chunks around it, or a caller who reads
        // DecimalColumn.Storage once and uses the matching narrowed accessor breaks on one batch.
        //
        // Never NARROWER than the precision, though: DecimalDecoder refuses that shape on the
        // vortex.decimal path and the rest of the arena assumes it, so take the max.
        DecimalStorageType precisionStorage = DecimalStorage.ForPrecision(dtype.Precision);
        int precisionWidth = DecimalStorage.ByteWidth(precisionStorage);
        int width = Math.Max(scalar.DecimalWidth, precisionWidth);
        DecimalStorageType storage = DecimalStorage.FromByteWidth(width);

        Span<byte> full = stackalloc byte[Int256.ByteCount];
        scalar.AsDecimal.Unscaled.WriteLittleEndianBytes(full);

        // The VALUE is checked against the precision's width whatever it is stored at: that stands
        // in for upstream's Scalar::try_new -> validate -> DecimalValue::fits_in_precision, which
        // rejects an over-precision constant at deserialization. Every byte above the precision's
        // width has to already be the sign extension, and the sign bit must survive. Widening from
        // there to `width` is then a pure sign extension.
        byte sign = (byte)((full[Int256.ByteCount - 1] & 0x80) != 0 ? 0xFF : 0x00);
        for (int i = precisionWidth; i < Int256.ByteCount; i++)
        {
            if (full[i] != sign)
            {
                throw new VortexFormatException(
                    $"A constant decimal does not fit its {precisionStorage} storage.");
            }
        }

        if (precisionWidth < Int256.ByteCount && (full[precisionWidth - 1] & 0x80) != (sign & 0x80))
        {
            throw new VortexFormatException(
                $"A constant decimal does not fit its {precisionStorage} storage.");
        }

        int bytes = ArrayDecodeContext.CheckedMultiply(length, width, "constant decimals");
        VortexBuffer values = CanonicalSupport.AllocateUninitialized(
            context, bytes, Align, out Span<byte> writable);
        RowKernels.Tile(writable, full[..width]);

        return context.Canonical.AddDecimal(
            dtype, length, validity, storage, dtype.Precision, dtype.Scale, values);
    }

    private static int BuildBinary(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar, Validity validity)
    {
        CanonicalArena arena = context.Canonical;
        ReadOnlySpan<byte> value = scalar.AsBinary;

        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, "constant views");
        VortexBuffer views = CanonicalSupport.AllocateUninitialized(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);

        if (value.Length <= CanonicalSupport.MaxInlineViewLength)
        {
            if (length > 0)
            {
                // WriteInlineView leaves the bytes past the value untouched, so the first view is
                // cleared before it is written and then tiled -- the zero-fill of one 16-byte view
                // rather than of the whole buffer.
                Span<byte> first = writable[..CanonicalSupport.ViewSize];
                first.Clear();
                CanonicalSupport.WriteInlineView(first, value);
                RowKernels.Tile(writable, first);
            }

            return arena.AddVarBinView(dtype, length, validity, views, default);
        }

        // The scalar's bytes live in the batch's ScalarStore, which is a managed array and may
        // move; a VortexBuffer is a raw pointer, so the value is copied into arena memory once and
        // every row references that.
        VortexBuffer data = CanonicalSupport.AllocateUninitialized(
            context, value.Length, Align, out Span<byte> heap);
        value.CopyTo(heap);
        if (length > 0)
        {
            Span<byte> first = writable[..CanonicalSupport.ViewSize];
            CanonicalSupport.WriteReferenceView(first, value.Length, value, bufferIndex: 0, offset: 0);
            RowKernels.Tile(writable, first);
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = data;
        return arena.AddVarBinView(dtype, length, validity, views, single);
    }

    private static int BuildStruct(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        scoped in TypedScalar scalar,
        Validity validity,
        int depth)
    {
        int fieldCount = dtype.FieldCount;
        if (scalar.ElementCount != fieldCount)
        {
            throw new VortexFormatException(
                $"A constant struct scalar carries {scalar.ElementCount} values for {fieldCount} fields.");
        }

        Span<int> stack = stackalloc int[StackSmall];
        Scratch<int> scratch = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> fields = scratch.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                TypedScalar element = scalar.GetElement(i);
                fields[i] = Build(context, dtype.GetField(i), length, in element, depth + 1);
            }

            return context.Canonical.AddStruct(dtype, length, validity, fields);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int BuildList(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        scoped in TypedScalar scalar,
        Validity validity,
        int depth)
    {
        CanonicalArena arena = context.Canonical;
        DType element = dtype.ElementType;
        int k = scalar.ElementCount;

        int elements = BuildPeriod(context, element, in scalar, k, depth);

        int bytes = ArrayDecodeContext.CheckedMultiply(length, 8, "constant list offsets");
        VortexBuffer offsets = CanonicalSupport.Allocate(context, bytes, Align, out Span<byte> _);
        VortexBuffer sizes = CanonicalSupport.Allocate(context, bytes, Align, out Span<byte> sizeSpan);

        // Offsets stay zero - every row is the same list, so every row points at the same run.
        if (length > 0)
        {
            CanonicalSupport.WriteInteger(sizeSpan, PType.U64, 0, k);
            RowKernels.Tile(sizeSpan, sizeSpan[..8]);
        }

        return arena.AddListView(
            dtype, length, validity, elements, offsets, PType.U64, sizes, PType.U64);
    }

    private static int BuildFixedSizeList(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        scoped in TypedScalar scalar,
        Validity validity,
        int depth)
    {
        DType element = dtype.ElementType;
        uint size = dtype.FixedSize;
        int k = scalar.ElementCount;
        if ((uint)k != size)
        {
            throw new VortexFormatException(
                $"A constant fixed-size-list scalar carries {k} values for a list of {size}.");
        }

        int total = FixedSizeListDecoder.ElementCount(length, size);
        int period = BuildPeriod(context, element, in scalar, k, depth);
        int elements = k == 0
            ? period
            : CanonicalConcat.Repeat(context, element, total, period, length);

        return context.Canonical.AddFixedSizeList(dtype, length, validity, elements, size);
    }

    /// <summary>
    /// Builds the <paramref name="k"/> element values of one list row as a single canonical node,
    /// by concatenating <paramref name="k"/> one-row constants.
    /// </summary>
    private static int BuildPeriod(
        ArrayDecodeContext context, DType element, scoped in TypedScalar scalar, int k, int depth)
    {
        if (k == 0)
        {
            return CanonicalFill.BuildZeroed(
                context, element, 0, Validity.FromNullability(element.Nullability));
        }

        Span<int> stack = stackalloc int[StackSmall];
        Scratch<int> scratch = new Scratch<int>(k, stack);
        try
        {
            Span<int> ones = scratch.Span;
            for (int j = 0; j < k; j++)
            {
                TypedScalar value = scalar.GetElement(j);
                ones[j] = Build(context, element, 1, in value, depth + 1);
            }

            return CanonicalConcat.Concat(context, element, k, ones);
        }
        finally
        {
            scratch.Dispose();
        }
    }
}
