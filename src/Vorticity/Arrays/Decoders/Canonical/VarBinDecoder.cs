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
internal sealed class VarBinDecoder : ArrayDecoder
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
    /// A take much sparser than the node decodes only the offsets its rows need, which a
    /// bit-packed offsets child unpacks block by block, and checks only those pairs. A denser take
    /// decodes and validates the offsets child whole, and
    /// <see cref="ArrayDecodeContext.IsNodeChecked"/> keeps that walk to once per node per scan
    /// rather than once per batch. Either way what is skipped is the per-row work: sixteen bytes of
    /// view, and a UTF-8 check, for every row of the node when a take wants a few of them.
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

        // Index 1 when present; DecodeValidity rejects any other child count for us.
        Validity validity = context.DecodeValidity(in node, 1, dtype.Nullability, length);
        if (selective)
        {
            validity = Compute.CanonicalFilter.FilterValidity(context.Canonical, validity, wanted);
        }

        VortexBuffer bytes = node.GetBuffer(0);
        ValidityMask mask = ValidityMask.From(context, validity);
        if (selective && (long)wanted.Length * SparseRows < length && !mask.AllInvalid
            && TrySparse(context, in node, offsetsDType, offsetsPType, offsetCount, wanted, bytes, dtype, validity, in mask, out int sparse))
        {
            return sparse;
        }

        int offsetsIndex = context.DecodeChild(in node, 0, offsetsDType, offsetCount);
        CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, offsetsPType, offsetCount, Id + " offsets");
        ReadOnlySpan<byte> offsetBytes = offsets.Values.Span;

        // "The offsets start at zero, never decrease and stay inside the heap" is a property of the
        // node, and a selective read visits the same node once per batch of the take, so the walk
        // is remembered per node. Outside a scope that remembers it, `IsNodeChecked` answers false
        // and every read walks the offsets again. A dense read of rows that are all valid checks
        // every offset as it cuts the heap, so it makes no walk of its own, and marks the node once
        // it has cut it.
        bool checksOffsets = !selective && mask.AllValid;
        bool checkedBefore = context.IsNodeChecked(in node);
        if (!checkedBefore)
        {
            RequireZeroStart(offsetBytes, offsetsPType);
            if (!checksOffsets)
            {
                ValidateOffsets(offsetBytes, offsetsPType, length, bytes.Length);
            }
        }

        int produced = selective ? wanted.Length : length;
        int viewBytes = ArrayDecodeContext.CheckedMultiply(
            produced, CanonicalSupport.ViewSize, Id + " views");

        // Uninitialized: `ViewKernels` writes all sixteen bytes of every view, the null rows'
        // `empty_view()` included, rather than inheriting the zeros from the allocator.
        VortexBuffer views = CanonicalSupport.AllocateUninitialized(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);

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
                dtype.Kind == DTypeKind.Utf8, in mask, Id);
        }

        if (!checkedBefore)
        {
            context.MarkNodeChecked(in node);
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
    /// A take of under a quarter of the node's rows reads its offsets apart. Measured on ten million
    /// texts of 12 to 16 bytes, bit-packed offsets: a pair per row is the faster at every density up
    /// to a fifth of the rows, 3.3 ms against 13 for a thousand, 58 against 63 for a fifth, and the
    /// two are even from a third on.
    /// </summary>
    private const int SparseRows = 4;

    /// <summary>
    /// A take of a few rows reads only the offsets its rows need -- the first, which must be zero,
    /// and each wanted row's and the next -- and checks only those pairs, as it cuts the views.
    /// </summary>
    /// <remarks>
    /// The whole-node walk it skips is a property of rows the take does not read; the pairs it
    /// reads are held to the heap and, for text, to UTF-8, which is all a view needs. A pair that
    /// fails gives the rows back to the whole read, which reports the fault by its row.
    /// </remarks>
    /// <returns>Whether the views were cut; the node's index in <paramref name="produced"/> when they were.</returns>
    private static bool TrySparse(
        ArrayDecodeContext context, in ArrayNode node, DType offsetsDType, PType offsetsPType, int offsetCount,
        ReadOnlySpan<int> wanted, VortexBuffer bytes, DType dtype, Validity validity, in ValidityMask mask,
        out int produced)
    {
        // Wanted rows ascend, so each row's first offset is the one the row before it ended at or
        // a new one, and its second always follows its first: row k spans offsets pairs[k] and
        // pairs[k] + 1 of those decoded, which is the selection the child is asked for.
        int count = wanted.Length;
        Span<int> neededStack = stackalloc int[129];
        Span<int> pairsStack = stackalloc int[64];
        using Scratch<int> needed = new Scratch<int>((2 * count) + 1, neededStack);
        using Scratch<int> pairs = new Scratch<int>(count, pairsStack);
        Span<int> rows = needed.Span;
        Span<int> starts = pairs.Span;
        int used = 1;
        rows[0] = 0;
        for (int k = 0; k < count; k++)
        {
            int row = wanted[k];
            if (row != rows[used - 1])
            {
                rows[used++] = row;
            }

            starts[k] = used - 1;
            rows[used++] = row + 1;
        }

        int offsetsIndex = context.DecodeChildSelected(in node, 0, offsetsDType, offsetCount, rows[..used]);
        CanonicalNode offsets = CanonicalSupport.RequirePrimitiveChild(
            context, offsetsIndex, offsetsPType, used, Id + " offsets");
        ReadOnlySpan<byte> offsetBytes = offsets.Values.Span;
        RequireZeroStart(offsetBytes, offsetsPType);

        int viewBytes = ArrayDecodeContext.CheckedMultiply(count, CanonicalSupport.ViewSize, Id + " views");
        VortexBuffer views = CanonicalSupport.AllocateUninitialized(
            context, viewBytes, CanonicalSupport.ViewSize, out Span<byte> writable);
        if (!ViewKernels.TryBuildFromPairs(
            offsetBytes, offsetsPType, bytes.Span, writable, starts, dtype.Kind == DTypeKind.Utf8, in mask))
        {
            produced = -1;
            return false;
        }

        Span<VortexBuffer> single = stackalloc VortexBuffer[1];
        single[0] = bytes;
        produced = bytes.Length == 0
            ? context.Canonical.AddVarBinView(dtype, count, validity, views, default)
            : context.Canonical.AddVarBinView(dtype, count, validity, views, single);
        return true;
    }

    /// <summary>Offsets must start at zero.</summary>
    private static void RequireZeroStart(ReadOnlySpan<byte> offsets, PType ptype)
    {
        long first = CanonicalSupport.ReadInteger(offsets, ptype, 0);
        if (first != 0)
        {
            throw new VortexFormatException(
                $"{Id} offsets must start at 0; this array starts at {first}.");
        }
    }

    /// <summary>
    /// Offsets must never decrease, and never leave the byte heap. A view's length is the
    /// difference of two consecutive offsets, so a decreasing pair would produce a length that
    /// reaches past the heap; both properties are checked before a single view is written.
    /// </summary>
    /// <remarks>
    /// The monotonicity check is a shifted compare, the same one <c>vortex.list</c>'s size vector
    /// and <c>vortex.patched</c>'s indices use, because reading each offset through a per-element
    /// type switch dominates a scan whose columns are variable-width.
    /// </remarks>
    private static void ValidateOffsets(
        ReadOnlySpan<byte> offsets, PType ptype, int length, int byteCount)
    {
        ViewKernels.RequireAscending(offsets, ptype, length + 1, Id);

        long last = CanonicalSupport.ReadInteger(offsets, ptype, length);
        if (last > byteCount)
        {
            throw new VortexFormatException(
                $"{Id} offsets end at {last}, past the {byteCount}-byte value heap.");
        }
    }
}
