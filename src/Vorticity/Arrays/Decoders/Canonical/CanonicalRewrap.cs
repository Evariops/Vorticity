// Re-publishes an already-decoded canonical node under a different dtype and validity, sharing
// every buffer and child. `vortex.masked` is the only Phase 1 encoding that needs it: its child is
// decoded at `P` with nullability stripped and the mask is then applied on top
// (vortex-array-0.86.1/src/arrays/masked/vtable/mod.rs).
//
// Nothing is copied. The new record points at the same VortexBuffers, which belong to the batch's
// segments or to the canonical arena's own rentals - either way to something whose lifetime is the
// batch (Phase 1 contract §2.2 rule 2).
using System;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Rebuilds a canonical node with a new dtype and validity, sharing its storage.</summary>
internal static class CanonicalRewrap
{
    private const int StackChildren = 32;

    /// <summary>
    /// Adds a node equal to <paramref name="sourceIndex"/> but carrying
    /// <paramref name="dtype"/> and <paramref name="validity"/>.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="sourceIndex">The already-decoded node to re-publish.</param>
    /// <param name="dtype">The dtype the result must carry.</param>
    /// <param name="validity">The validity the result must carry.</param>
    /// <param name="length">Row count; must equal the source's.</param>
    /// <returns>The new canonical node's index.</returns>
    /// <exception cref="VortexFormatException">The source's row count disagrees with the parent's.</exception>
    internal static int WithValidity(
        ArrayDecodeContext context, int sourceIndex, DType dtype, Validity validity, int length)
    {
        CanonicalArena arena = context.Canonical;
        CanonicalNode source = arena.GetNode(sourceIndex);
        if (source.Length != length)
        {
            throw new VortexFormatException(
                $"A masked child of {source.Length} rows cannot describe an array of {length}.");
        }

        switch (source.Kind)
        {
            case CanonicalKind.Null:
                return arena.AddNull(dtype, length);

            case CanonicalKind.Bool:
                return arena.AddBool(dtype, length, validity, source.Bits, source.BitOffset);

            case CanonicalKind.Primitive:
                return arena.AddPrimitive(dtype, length, validity, source.PType, source.Values);

            case CanonicalKind.Decimal:
                return arena.AddDecimal(
                    dtype, length, validity, source.Storage, source.Precision, source.Scale, source.Values);

            case CanonicalKind.ListView:
                return arena.AddListView(
                    dtype, length, validity, source.ElementsIndex,
                    source.Offsets, source.OffsetPType, source.Sizes, source.SizePType);

            case CanonicalKind.FixedSizeList:
                return arena.AddFixedSizeList(
                    dtype, length, validity, source.ElementsIndex, source.FixedSize);

            case CanonicalKind.VarBinView:
                return RewrapVarBinView(arena, in source, dtype, validity, length);

            case CanonicalKind.Struct:
                return RewrapStruct(arena, in source, dtype, validity, length);

            default:
                // Extension: its validity is the storage's, so the mask has to be pushed down one
                // level rather than applied to the wrapper.
                return RewrapExtension(context, in source, dtype, validity, length);
        }
    }

    private static int RewrapVarBinView(
        CanonicalArena arena, in CanonicalNode source, DType dtype, Validity validity, int length)
    {
        int count = source.DataBufferCount;
        Span<VortexBuffer> stack = stackalloc VortexBuffer[StackChildren];
        Scratch<VortexBuffer> scratch = new Scratch<VortexBuffer>(count, stack);
        try
        {
            Span<VortexBuffer> buffers = scratch.Span;
            for (int i = 0; i < count; i++)
            {
                buffers[i] = source.GetDataBuffer(i);
            }

            return arena.AddVarBinView(dtype, length, validity, source.Views, buffers);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int RewrapStruct(
        CanonicalArena arena, in CanonicalNode source, DType dtype, Validity validity, int length)
    {
        int count = source.FieldCount;
        Span<int> stack = stackalloc int[StackChildren];
        Scratch<int> scratch = new Scratch<int>(count, stack);
        try
        {
            Span<int> fields = scratch.Span;
            for (int i = 0; i < count; i++)
            {
                fields[i] = source.GetFieldIndex(i);
            }

            return arena.AddStruct(dtype, length, validity, fields);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int RewrapExtension(
        ArrayDecodeContext context, in CanonicalNode source, DType dtype, Validity validity, int length)
    {
        if (dtype.Kind != DTypeKind.Extension)
        {
            throw new VortexFormatException(
                $"An Extension child cannot be re-published as a {dtype.Kind} array.");
        }

        int storage = WithValidity(context, source.StorageIndex, dtype.StorageType, validity, length);
        return context.Canonical.AddExtension(dtype, length, storage);
    }
}
