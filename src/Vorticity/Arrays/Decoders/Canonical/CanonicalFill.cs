using System;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Zero-filled canonical nodes, for all-null and zero-row arrays.</summary>
/// <remarks>
/// The recursion walks the dtype rather than any file data, so its depth is the dtype's depth,
/// which parsing has already bounded; the depth budget here is a second guard.
/// </remarks>
internal static class CanonicalFill
{
    private const int Align = CanonicalSupport.MaxRequiredAlignment;
    private const int StackFields = 32;

    /// <summary>
    /// Builds a canonical node of <paramref name="dtype"/> and <paramref name="length"/> rows whose
    /// value bytes are all zero.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The dtype to build.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">The validity the result carries.</param>
    /// <returns>The new canonical node's index.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="dtype"/> is a Map, Union or Variant, which have no zeroed canonical form.
    /// </exception>
    internal static int BuildZeroed(
        ArrayDecodeContext context, DType dtype, int length, Validity validity) =>
        BuildZeroed(context, dtype, length, validity, depth: 1);

    /// <summary>Whether <see cref="BuildZeroed(ArrayDecodeContext, DType, int, Validity)"/> can build <paramref name="dtype"/>.</summary>
    /// <param name="dtype">The dtype.</param>
    internal static bool CanBuild(DType dtype) => CanBuild(dtype, depth: 1);

    private static bool CanBuild(DType dtype, int depth)
    {
        if (depth > VortexLimits.MaxDTypeDepth)
        {
            return false;
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Null:
            case DTypeKind.Bool:
            case DTypeKind.Primitive:
            case DTypeKind.Decimal:
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                return true;
            case DTypeKind.List:
            case DTypeKind.FixedSizeList:
                return CanBuild(dtype.ElementType, depth + 1);
            case DTypeKind.Extension:
                return CanBuild(dtype.StorageType, depth + 1);
            case DTypeKind.Struct:
                for (int i = 0; i < dtype.FieldCount; i++)
                {
                    if (!CanBuild(dtype.GetField(i), depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private static int BuildZeroed(
        ArrayDecodeContext context, DType dtype, int length, Validity validity, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Canonical fill");

        CanonicalArena arena = context.Canonical;
        if (dtype.IsDefault)
        {
            throw new VortexFormatException("A canonical array cannot be built without a dtype.");
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Null:
                return arena.AddNull(dtype, length);

            case DTypeKind.Bool:
            {
                VortexBuffer bits = CanonicalSupport.Allocate(
                    context, CanonicalSupport.BitmapByteCount(length), Align);
                return arena.AddBool(dtype, length, validity, bits, 0);
            }

            case DTypeKind.Primitive:
            {
                PType ptype = dtype.PType;
                int bytes = ArrayDecodeContext.CheckedMultiply(length, ptype.ByteWidth(), "values");
                return arena.AddPrimitive(
                    dtype, length, validity, ptype,
                    CanonicalSupport.Allocate(context, bytes, Align));
            }

            case DTypeKind.Decimal:
            {
                DecimalStorageType storage = DecimalStorage.ForPrecision(dtype.Precision);
                int bytes = ArrayDecodeContext.CheckedMultiply(
                    length, DecimalStorage.ByteWidth(storage), "decimal values");
                return arena.AddDecimal(
                    dtype, length, validity, storage, dtype.Precision, dtype.Scale,
                    CanonicalSupport.Allocate(context, bytes, Align));
            }

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
            {
                // A zeroed view is the empty view, so no data buffer is needed at all.
                int bytes = ArrayDecodeContext.CheckedMultiply(
                    length, CanonicalSupport.ViewSize, "views");
                VortexBuffer views = CanonicalSupport.Allocate(context, bytes, CanonicalSupport.ViewSize);
                return arena.AddVarBinView(dtype, length, validity, views, default);
            }

            case DTypeKind.List:
            {
                DType element = dtype.ElementType;
                int elements = BuildZeroed(
                    context, element, 0, Validity.FromNullability(element.Nullability), depth + 1);
                int bytes = ArrayDecodeContext.CheckedMultiply(length, 8, "list offsets");
                return arena.AddListView(
                    dtype, length, validity, elements,
                    CanonicalSupport.Allocate(context, bytes, Align), PType.U64,
                    CanonicalSupport.Allocate(context, bytes, Align), PType.U64);
            }

            case DTypeKind.FixedSizeList:
            {
                DType element = dtype.ElementType;
                int count = FixedSizeListDecoder.ElementCount(length, dtype.FixedSize);
                int elements = BuildZeroed(
                    context, element, count, Validity.FromNullability(element.Nullability), depth + 1);
                return arena.AddFixedSizeList(dtype, length, validity, elements, dtype.FixedSize);
            }

            case DTypeKind.Struct:
                return BuildZeroedStruct(context, dtype, length, validity, depth);

            case DTypeKind.Extension:
            {
                DType storage = dtype.StorageType;
                int child = BuildZeroed(context, storage, length, validity, depth + 1);
                return arena.AddExtension(dtype, length, child);
            }

            default:
                throw new VortexFormatException(
                    $"There is no zeroed canonical form for a {dtype.Kind} dtype.");
        }
    }

    private static int BuildZeroedStruct(
        ArrayDecodeContext context, DType dtype, int length, Validity validity, int depth)
    {
        int fieldCount = dtype.FieldCount;
        Span<int> stack = stackalloc int[StackFields];
        Scratch<int> scratch = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> fields = scratch.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                DType field = dtype.GetField(i);
                fields[i] = BuildZeroed(
                    context, field, length, Validity.FromNullability(field.Nullability), depth + 1);
            }

            return context.Canonical.AddStruct(dtype, length, validity, fields);
        }
        finally
        {
            scratch.Dispose();
        }
    }
}
