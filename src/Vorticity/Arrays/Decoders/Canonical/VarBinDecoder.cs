using System;
using System.Text.Unicode;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.varbin</c> into the canonical <c>VarBinView</c> form. VarBin has no canonical
/// form of its own, so the conversion happens in the decoder and nothing downstream ever sees an
/// offsets-and-bytes array.
/// </summary>
public sealed class VarBinDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.varbin";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly VarBinDecoder Instance = new VarBinDecoder();

    private VarBinDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.varbin"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.VarBin;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Builds views for the rows a take asks for, and for no others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offsets child is still decoded and validated whole: it is a <c>vortex.primitive</c> read
    /// zero-copy, so decoding it is a buffer wrap, and
    /// <see cref="ArrayDecodeContext.IsNodeChecked"/> keeps the offset walk to once per node per
    /// scan rather than once per batch. What this skips is the per-row work: sixteen bytes of view,
    /// and a UTF-8 check, for every row of the node when a take wants a few of them.
    /// </para>
    /// <para>
    /// It exists because <c>vortex.parquet.variant</c> declares itself selective. A root that
    /// answers <see cref="SelectsWithoutFullDecode"/> takes the flat layout reader's pushed route,
    /// which has no retained-chunk cache behind it, so a child without a selective path would
    /// decode its whole node once per batch.
    /// </para>
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        VarBinMetadata metadata = VarBinMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireBinaryLike(dtype, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, 2, Id);

        PType offsetsPType = metadata.OffsetsPType;
        CanonicalSupport.RequireIntegerPType(offsetsPType, Id + " offsets_ptype");

        int offsetCount = ArrayDecodeContext.CheckedLength((ulong)length + 1, Id + " offset count");
        DType offsetsDType = context.Types.Primitive(offsetsPType, Nullability.NonNullable);
        int offsetsIndex = context.DecodeChild(in node, 0, offsetsDType, offsetCount);

        // Index 1 when present; DecodeValidity rejects any other child count for us.
        Validity validity = context.DecodeValidity(in node, 1, dtype.Nullability, length);
        if (selective)
        {
            validity = Compute.CanonicalFilter.FilterValidity(context.Canonical, validity, wanted);
        }

        CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, offsetsPType, offsetCount, Id + " offsets");

        VortexBuffer bytes = node.GetBuffer(0);
        ReadOnlySpan<byte> offsetBytes = offsets.Values.Span;

        // "The offsets start at zero, never decrease and stay inside the heap" is a property of the
        // node, and a selective read visits the same node once per batch of the take, so the walk
        // is remembered per node. Outside a scope that remembers it, `IsNodeChecked` answers false
        // and every read walks the offsets again.
        if (!context.IsNodeChecked(in node))
        {
            ValidateOffsets(offsetBytes, offsetsPType, length, bytes.Length);
            context.MarkNodeChecked(in node);
        }

        int produced = selective ? wanted.Length : length;
        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            produced, CanonicalSupport.ViewSize, Id + " views");

        // Uninitialized: `ViewKernels` writes all sixteen bytes of every view, the null rows'
        // `empty_view()` included, rather than inheriting the zeros from the allocator.
        VortexBuffer views = CanonicalSupport.AllocateUninitialized(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);

        ValidityMask mask = ValidityMask.From(context, validity);
        if (mask.AllInvalid)
        {
            // Every row is null, so every view is `empty_view()` and no offset is read.
            writable.Clear();
        }
        else if (selective)
        {
            ViewKernels.BuildFromOffsetsSelected(
                offsetBytes, offsetsPType, bytes.Span, writable, wanted,
                dtype.Kind == DTypeKind.Utf8, in mask);
        }
        else
        {
            ViewKernels.BuildFromOffsets(
                offsetBytes, offsetsPType, bytes.Span, writable, length,
                dtype.Kind == DTypeKind.Utf8, in mask);
        }

        if (bytes.Length == 0)
        {
            // Every value is empty, so no view can reference a buffer; matching upstream, which
            // returns `Vec::new()` for an empty heap.
            return context.Canonical.AddVarBinView(dtype, produced, validity, views, default);
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = bytes;
        return context.Canonical.AddVarBinView(dtype, produced, validity, views, single);
    }

    /// <summary>
    /// Offsets must start at zero, never decrease, and never leave the byte heap. A view's length
    /// is the difference of two consecutive offsets, so a decreasing pair would produce a length
    /// that reaches past the heap; all three properties are checked before a single view is written.
    /// </summary>
    /// <remarks>
    /// The monotonicity check is a shifted compare, the same one <c>vortex.list</c>'s size vector
    /// and <c>vortex.patched</c>'s indices use, because reading each offset through a per-element
    /// type switch dominates a scan whose columns are variable-width.
    /// </remarks>
    private static void ValidateOffsets(
        ReadOnlySpan<byte> offsets, PType ptype, int length, int byteCount)
    {
        long first = CanonicalSupport.ReadInteger(offsets, ptype, 0);
        if (first != 0)
        {
            throw new VortexFormatException(
                $"{Id} offsets must start at 0; this array starts at {first}.");
        }

        ViewKernels.RequireAscending(offsets, ptype, length + 1, Id);

        long last = CanonicalSupport.ReadInteger(offsets, ptype, length);
        if (last > byteCount)
        {
            throw new VortexFormatException(
                $"{Id} offsets end at {last}, past the {byteCount}-byte value heap.");
        }
    }
}
