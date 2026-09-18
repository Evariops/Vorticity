// vortex.varbin - vortex-array-0.86.1/src/arrays/varbin/vtable/mod.rs `deserialize`, then
// vortex-array-0.86.1/src/arrays/varbinview/build_views.rs `build_views_from_offsets`.
//
// VarBin has no canonical form of its own: `Canonical::VarBin` does not exist and upstream
// canonicalizes it to a VarBinView. Phase 1 contract §9.2 requires the conversion to happen HERE,
// in the decoder, so nothing downstream ever sees an offsets-and-bytes array.
//
// The offsets are class I. Upstream documents "monotonically non-decreasing", "the first value
// must be 0" and "no offset may exceed bytes.len()" as invariants of `new_unchecked` but its
// `validate` only enforces the last of the three, and `build_views_from_offsets` computes lengths
// with a `wrapping_sub`. A non-monotone pair there produces a huge wrapped length that then indexes
// out of the byte heap, so all three are checked before a single view is written.
using System;
using System.Text.Unicode;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.varbin</c> into the canonical <c>VarBinView</c> form.</summary>
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
    /// The rows a take asks for, WITHOUT building a view for the million it did not ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PERF-GAPS.md V2. The offsets child is still decoded whole and still validated whole -- it is
    /// a <c>vortex.primitive</c> read zero-copy, so "decoding" it is a buffer wrap, and
    /// <see cref="ArrayDecodeContext.IsNodeChecked"/> makes the O(n) offset walk run once per node
    /// per scan rather than once per batch (v2 R26). What this skips is the part that is actually
    /// expensive and actually per-row: sixteen bytes of view, and a UTF-8 check, for every row of
    /// the node. On the corpus's `parquet_variant` that is two columns of a million views, 32 MB,
    /// built to return sixty-four rows.
    /// </para>
    /// <para>
    /// IT EXISTS BECAUSE <c>vortex.parquet.variant</c> DECLARES ITSELF SELECTIVE. A root that
    /// answers <see cref="SelectsWithoutFullDecode"/> takes `FlatLayoutReader`'s pushed route, which
    /// has no retained-chunk cache behind it, so an unspecialized child would decode its whole node
    /// once PER BATCH -- the quadratic take of v2 R23. The two changes are one change.
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

        // WALKED ONCE PER NODE, NOT ONCE PER BATCH. "The offsets start at zero, never decrease and
        // stay inside the heap" is a property of the NODE, and a selective read visits the same
        // node once per batch of the take. v2 R26's scope is exactly for this; outside it
        // `IsNodeChecked` answers false and every scan walks as before.
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
    /// Class I: offsets must start at zero, never decrease, and never leave the byte heap.
    /// </summary>
    /// <remarks>
    /// THE MONOTONICITY CHECK IS A SHIFTED COMPARE, and the same one `vortex.list`'s size vector
    /// and `vortex.patched`'s indices use. It was a `ReadInteger` switch twice per row -- 16% of a
    /// 1M-row `vortex.parquet.variant` scan, which is two varbin columns and nothing else.
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
