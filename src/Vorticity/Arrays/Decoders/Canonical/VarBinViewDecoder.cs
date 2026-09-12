// vortex.varbinview - vortex-array-0.86.1/src/arrays/varbinview/vtable/mod.rs `deserialize`.
//
// Two things decide correctness here.
//
// 1. The `views` buffer is the LAST one, not the first: upstream is
//    `let Some((views_handle, data_handles)) = buffers.split_last() else { bail }`, so a node with
//    N + 1 buffers has N data buffers and there may be zero of them. Reading buffer 0 as the views
//    would read a data buffer as 16-byte views and produce plausible garbage.
// 2. Every view a consumer may dereference is validated first (class I, docs/08-semantics.md §5):
//    a reference view needs `buffer_index < N`, `offset + size <= buffers[buffer_index].len()`,
//    and a 4-byte prefix equal to the first four bytes of the value it references
//    (`validate_view`, vortex-array-0.86.1/src/arrays/varbinview/array.rs). The prefix is not a
//    hint: it is a redundant copy the Arrow columnar spec requires, and it is what makes
//    prefix-first comparison sound for anyone reading `CanonicalNode.Views`.
//    NULL rows are deliberately NOT validated - upstream skips them too
//    (`VarBinViewArray::validate`) because a null slot's view is garbage by construction. Their
//    contract is that no consumer dereferences a null row.
using System;
using System.Text.Unicode;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.varbinview</c>: Arrow-style 16-byte views over N data buffers.</summary>
public sealed class VarBinViewDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.varbinview";

    private const int StackBuffers = 8;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly VarBinViewDecoder Instance = new VarBinViewDecoder();

    private VarBinViewDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.varbinview"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.VarBinView;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        CanonicalSupport.RequireBinaryLike(dtype, Id);

        int bufferCount = node.BufferCount;
        if (bufferCount < 1)
        {
            throw new VortexFormatException(
                $"{Id} requires at least 1 buffer (the views); this node has {bufferCount}.");
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        int dataBufferCount = bufferCount - 1;
        VortexBuffer views = node.GetBuffer(dataBufferCount);
        CanonicalSupport.RequireExactBuffer(views, length, CanonicalSupport.ViewSize, Id + " views");

        Span<VortexBuffer> stack = stackalloc VortexBuffer[StackBuffers];
        Scratch<VortexBuffer> data = new Scratch<VortexBuffer>(dataBufferCount, stack);
        try
        {
            Span<VortexBuffer> buffers = data.Span;
            for (int i = 0; i < dataBufferCount; i++)
            {
                buffers[i] = node.GetBuffer(i);
            }

            ValidateViews(context, views.Span, buffers, validity, length, dtype.Kind == DTypeKind.Utf8);
            return context.Canonical.AddVarBinView(dtype, length, validity, views, buffers);
        }
        finally
        {
            data.Dispose();
        }
    }

    /// <summary>
    /// Bounds-checks (and, for Utf8, UTF-8-checks) every view a consumer may dereference.
    /// </summary>
    internal static void ValidateViews(
        ArrayDecodeContext context,
        ReadOnlySpan<byte> views,
        ReadOnlySpan<VortexBuffer> dataBuffers,
        Validity validity,
        int length,
        bool requireUtf8)
    {
        ValidityMask mask = ValidityMask.From(context, validity);
        if (mask.AllInvalid)
        {
            return;
        }

        bool allValid = mask.AllValid;
        for (int i = 0; i < length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                continue;
            }

            ReadOnlySpan<byte> view = views.Slice(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);

            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                // Inlined: the value is the first `size` of the 12 bytes that follow, so it can
                // never escape the view. Only the UTF-8 rule can fail.
                if (requireUtf8 && !Utf8.IsValid(view.Slice(4, (int)size)))
                {
                    ThrowNotUtf8(i);
                }

                continue;
            }

            uint bufferIndex = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);

            if (bufferIndex >= (uint)dataBuffers.Length)
            {
                ThrowBufferIndex(i, bufferIndex, dataBuffers.Length);
            }

            VortexBuffer target = dataBuffers[(int)bufferIndex];
            if ((ulong)offset + size > (ulong)(uint)target.Length)
            {
                ThrowViewRange(i, offset, size, target.Length);
            }

            // size > 12 on this path, so the value always has at least the four prefix bytes, and
            // the bounds check above puts both casts inside an int-length buffer.
            ReadOnlySpan<byte> value = target.Span.Slice((int)offset, (int)size);
            if (!view.Slice(4, 4).SequenceEqual(value[..4]))
            {
                ThrowPrefixMismatch(i);
            }

            if (requireUtf8 && !Utf8.IsValid(value))
            {
                ThrowNotUtf8(i);
            }
        }
    }

    private static void ThrowPrefixMismatch(int row) =>
        throw new VortexFormatException(
            $"Row {row}'s view prefix does not match the first four bytes of the value it " +
            "references.");

    private static void ThrowNotUtf8(int row) =>
        throw new VortexFormatException($"Row {row} of a Utf8 array is not valid UTF-8.");

    private static void ThrowBufferIndex(int row, uint bufferIndex, int count) =>
        throw new VortexFormatException(
            $"Row {row} references data buffer {bufferIndex}; the array has {count}.");

    private static void ThrowViewRange(int row, uint offset, uint size, int bufferLength) =>
        throw new VortexFormatException(
            $"Row {row} spans [{offset}, {(ulong)offset + size}) of a data buffer holding " +
            $"{bufferLength} bytes.");
}
