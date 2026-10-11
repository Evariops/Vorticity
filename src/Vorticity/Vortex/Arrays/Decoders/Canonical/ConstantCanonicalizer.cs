using System;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.Types.Variant;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Builds the canonical form of a repeated scalar.</summary>
/// <remarks>
/// A list is the one case that is not a single node: a list view's offsets are arbitrary, so every
/// row of a constant list points at one shared copy of the row's elements, which is work
/// proportional to one row rather than to the column. A fixed-size list is positional and has no
/// offsets, so there the elements really are tiled once per row.
/// </remarks>
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

        // An extension's scalar is interpreted against its storage dtype, which is exactly what
        // the scalar reader has already unwrapped it to.
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

                // The element is written once instead of once per row: a constant column stores
                // one value and a count, not a value repeated across the whole buffer.
                int bytes = ArrayDecodeContext.CheckedMultiply(
                    length, ptype.ByteWidth(), "constant values");

                // The ceiling is charged against what the node stands for, not against the handful
                // of bytes it stores.
                CanonicalSupport.RequireStandsForWithinBudget(context, bytes);

                int width = ptype.ByteWidth();
                Span<byte> element = stackalloc byte[width];
                scalar.WriteTo(element, ptype);
                return arena.AddConstant(dtype, length, validity, element);
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

            case DTypeKind.Variant:
                return BuildVariant(context, dtype, length, in scalar, validity);

            default:
                throw new VortexFormatException(
                    $"There is no canonical form for a constant {dtype.Kind} column.");
        }
    }

    /// <summary>
    /// Builds a constant variant column: <c>Struct{metadata, value}</c> wearing the variant dtype.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A variant over a constant carrier holds a typed scalar, while the canonical form every
    /// variant column takes here is the binary metadata-and-value pair. This is therefore the one
    /// place that has to encode rather than decode, and it encodes the primitives only: an object
    /// or an array raises rather than being approximated.
    /// </para>
    /// <para>
    /// The two columns are themselves constants -- every row is the same bytes -- so they are built
    /// by tiling one view, which is what `BuildBinary` already does.
    /// </para>
    /// </remarks>
    private static int BuildVariant(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar,
        Validity validity)
    {
        if (scalar.WireKind != ScalarValueKind.Variant)
        {
            throw new VortexFormatException(
                $"A constant variant column needs a variant scalar; the file carries " +
                $"{scalar.WireKind}.");
        }

        Scalar nested = scalar.AsVariantScalar;
        DType nestedType = nested.DType;
        TypedScalar inner = TypedScalarReader.Interpret(nested.Value, nestedType);

        int valueBytes = ParquetVariant.MeasureValue(in inner, nestedType);
        if (valueBytes < 0)
        {
            throw new VortexUnsupportedException(
                "vortex.variant",
                VortexComponentKind.Array,
                $"a constant variant of dtype {nestedType} has no primitive encoding here; null, " +
                "booleans, integers, floats, strings and binary do.");
        }

        Span<byte> stack = stackalloc byte[64];
        Scratch<byte> scratch = new Scratch<byte>(valueBytes, stack);
        try
        {
            ParquetVariant.WriteValue(in inner, nestedType, scratch.Span);

            DType binary = context.Types.Binary(Nullability.NonNullable);
            int metadataField = BuildConstantBinary(
                context, binary, length, ParquetVariant.EmptyMetadata);
            int valueField = BuildConstantBinary(context, binary, length, scratch.Span);

            Span<int> fields = stackalloc int[2];
            fields[0] = metadataField;
            fields[1] = valueField;
            return context.Canonical.AddStruct(dtype, length, validity, fields);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>A binary column of <paramref name="length"/> rows all holding the same bytes.</summary>
    private static int BuildConstantBinary(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<byte> value)
    {
        CanonicalArena arena = context.Canonical;
        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, "constant variant views");

        CanonicalSupport.RequireStandsForWithinBudget(context, viewBytes);
        return arena.AddConstant(dtype, length, Validity.NonNullable, value);
    }

    private static int BuildDecimal(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar, Validity validity)
    {
        // The storage width comes from the scalar, not from the precision: a decimal column may be
        // stored wider than its precision needs, and a chunk a compressor folded to a constant has
        // to report the same width as the chunks around it. Otherwise a caller who reads the
        // column's storage once and picks the matching narrowed accessor breaks on that one batch.
        //
        // Never narrower than the precision, though: the decimal decoder refuses that shape and
        // the rest of the arena assumes it, so take the wider of the two.
        DecimalStorageType precisionStorage = DecimalStorage.ForPrecision(dtype.Precision);
        int precisionWidth = DecimalStorage.ByteWidth(precisionStorage);
        int width = Math.Max(scalar.DecimalWidth, precisionWidth);

        Span<byte> full = stackalloc byte[Int256.ByteCount];
        scalar.AsDecimal.Unscaled.WriteLittleEndianBytes(full);

        // The value is checked against the precision's width whatever it is stored at, so that an
        // over-precision constant is rejected here rather than travelling on. Every byte above the
        // precision's width has to already be the sign extension, and the sign bit must survive;
        // widening from there is then a pure sign extension.
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

        // The same form the other kinds take, once the checks above have passed: one element and a
        // count rather than an element a row. The storage type is not carried on the record
        // because the element's width already says what it is.
        int bytes = ArrayDecodeContext.CheckedMultiply(length, width, "constant decimals");
        CanonicalSupport.RequireStandsForWithinBudget(context, bytes);
        return context.Canonical.AddConstant(dtype, length, validity, full[..width]);
    }

    private static int BuildBinary(
        ArrayDecodeContext context, DType dtype, int length, scoped in TypedScalar scalar, Validity validity)
    {
        CanonicalArena arena = context.Canonical;
        ReadOnlySpan<byte> value = scalar.AsBinary;

        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            length, CanonicalSupport.ViewSize, "constant views");

        // The string half matters most for a constant variant column, which is two constant binary
        // columns: tiling them would build a full view buffer twice over, for two values that
        // never change.
        CanonicalSupport.RequireStandsForWithinBudget(context, viewBytes);
        return arena.AddConstant(dtype, length, validity, value);
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
