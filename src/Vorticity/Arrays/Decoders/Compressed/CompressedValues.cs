// The value plumbing shared by the four compressed decoders that MOVE canonical rows around
// rather than compute them: vortex.dict gathers, vortex.runend repeats, vortex.sparse scatters
// over a fill, and fastlanes.rle gathers per chunk. Writing that four times would be four chances
// to get the VarBinView buffer-index remap or the validity collapse subtly different.
//
// Four canonical kinds can be moved this way, which is exactly the set upstream's own run-end and
// sparse canonicalizers handle: Primitive, Decimal, Bool and VarBinView
// (vortex-runend-0.86.1/src/array.rs `run_end_canonicalize`,
// vortex-sparse-0.86.1/src/canonical.rs). A Struct, ListView, FixedSizeList or Extension under one
// of these encodings is rejected, as it is upstream.
using System;
using System.Buffers;
using System.Buffers.Binary;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>A read-only view of one canonical node's value container, row-addressable.</summary>
internal readonly ref struct ValueReader
{
    /// <summary>Bytes in one Arrow-style binary view.</summary>
    public const int ViewWidth = 16;

    private readonly ReadOnlySpan<byte> _bytes;

    private ValueReader(
        CanonicalKind kind,
        int width,
        int length,
        ReadOnlySpan<byte> bytes,
        int bitOffset,
        PType ptype,
        DecimalStorageType storage,
        byte precision,
        sbyte scale,
        int dataBufferCount,
        int nodeIndex,
        Validity validity)
    {
        Kind = kind;
        Width = width;
        Length = length;
        _bytes = bytes;
        BitOffset = bitOffset;
        PType = ptype;
        Storage = storage;
        Precision = precision;
        Scale = scale;
        DataBufferCount = dataBufferCount;
        NodeIndex = nodeIndex;
        Validity = validity;
    }

    /// <summary>The canonical form: Primitive, Decimal, Bool or VarBinView.</summary>
    public CanonicalKind Kind { get; }

    /// <summary>Bytes per row, or 0 for <see cref="CanonicalKind.Bool"/>, which is bit-packed.</summary>
    public int Width { get; }

    /// <summary>Row count.</summary>
    public int Length { get; }

    /// <summary>The values, the views, or the bitmap.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Bit position of row 0 in <see cref="Bytes"/>; 0 for every kind but Bool.</summary>
    public int BitOffset { get; }

    /// <summary>Physical type, for Primitive.</summary>
    public PType PType { get; }

    /// <summary>Storage width, for Decimal.</summary>
    public DecimalStorageType Storage { get; }

    /// <summary>Precision, for Decimal.</summary>
    public byte Precision { get; }

    /// <summary>Scale, for Decimal.</summary>
    public sbyte Scale { get; }

    /// <summary>Data buffers the views may reference, for VarBinView.</summary>
    public int DataBufferCount { get; }

    /// <summary>The canonical node this reads.</summary>
    public int NodeIndex { get; }

    /// <summary>The node's validity.</summary>
    public Validity Validity { get; }

    /// <summary>Describes canonical node <paramref name="nodeIndex"/>.</summary>
    /// <param name="arena">The arena holding it.</param>
    /// <param name="nodeIndex">The node.</param>
    /// <param name="encodingId">The encoding asking, for the error message.</param>
    /// <exception cref="VortexFormatException">The node's kind cannot be moved row by row.</exception>
    public static ValueReader Of(CanonicalArena arena, int nodeIndex, string encodingId)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        switch (node.Kind)
        {
            case CanonicalKind.Primitive:
            {
                int width = node.PType.ByteWidth();
                return new ValueReader(
                    CanonicalKind.Primitive, width, node.Length, node.Values.Span, 0,
                    node.PType, default, 0, 0, 0, nodeIndex, node.Validity);
            }

            case CanonicalKind.Decimal:
            {
                int width = DecimalStorage.ByteWidth(node.Storage);
                return new ValueReader(
                    CanonicalKind.Decimal, width, node.Length, node.Values.Span, 0,
                    default, node.Storage, node.Precision, node.Scale, 0, nodeIndex, node.Validity);
            }

            case CanonicalKind.Bool:
                return new ValueReader(
                    CanonicalKind.Bool, 0, node.Length, node.Bits.Span, node.BitOffset,
                    default, default, 0, 0, 0, nodeIndex, node.Validity);

            case CanonicalKind.VarBinView:
                return new ValueReader(
                    CanonicalKind.VarBinView, ViewWidth, node.Length, node.Views.Span, 0,
                    default, default, 0, 0, node.DataBufferCount, nodeIndex, node.Validity);

            default:
                // CompressedThrow.Format<T> cannot be instantiated with a ref struct, so the
                // throw is a statement and the return is unreachable.
                CompressedThrow.Format(
                    $"{encodingId} cannot expand a {node.Kind} value array; Primitive, Decimal, " +
                    "Bool and VarBinView are the whole domain.");
                return default;
        }
    }
}

/// <summary>
/// Materializes one canonical node of the same kind as a <see cref="ValueReader"/>, row by row.
/// Every byte comes from <see cref="CanonicalArena.Allocate(int, int)"/>, which is the only place
/// a decoder may get writable memory (Phase 1 contract §8.4).
/// </summary>
internal ref struct ValueWriter
{
    private readonly CanonicalKind _kind;
    private readonly int _width;
    private readonly int _length;
    private readonly PType _ptype;
    private readonly DecimalStorageType _storage;
    private readonly byte _precision;
    private readonly sbyte _scale;
    private readonly int _bufferIndexShift;
    private readonly VortexBuffer _buffer;
    private readonly Span<byte> _bytes;

    private ValueWriter(
        CanonicalKind kind,
        int width,
        int length,
        PType ptype,
        DecimalStorageType storage,
        byte precision,
        sbyte scale,
        int bufferIndexShift,
        VortexBuffer buffer,
        Span<byte> bytes)
    {
        _kind = kind;
        _width = width;
        _length = length;
        _ptype = ptype;
        _storage = storage;
        _precision = precision;
        _scale = scale;
        _bufferIndexShift = bufferIndexShift;
        _buffer = buffer;
        _bytes = bytes;
    }

    /// <summary>The canonical form being built.</summary>
    public readonly CanonicalKind Kind => _kind;

    /// <summary>Bytes per row; 0 for Bool.</summary>
    public readonly int Width => _width;

    /// <summary>The writable value bytes.</summary>
    public readonly Span<byte> Bytes => _bytes;

    /// <summary>
    /// Allocates the output container for <paramref name="length"/> rows shaped like
    /// <paramref name="template"/>.
    /// </summary>
    /// <param name="ctx">The decode context; its canonical arena owns the memory.</param>
    /// <param name="template">The node whose kind and element shape the output copies.</param>
    /// <param name="length">Row count, already validated against the node's declared length.</param>
    /// <param name="bufferIndexShift">
    /// Added to the buffer index of every non-inline VarBinView view copied through
    /// <see cref="Copy"/>. Non-zero only when the caller prepends its own data buffer, which is
    /// what <c>vortex.sparse</c> does for a long fill value.
    /// </param>
    /// <param name="encodingId">The encoding asking, for the error messages.</param>
    /// <exception cref="VortexFormatException">
    /// The output size overflows a 32-bit length or exceeds the decompression ceiling.
    /// </exception>
    public static ValueWriter Create(
        ArrayDecodeContext ctx, in ValueReader template, int length, int bufferIndexShift,
        string encodingId)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (template.Kind == CanonicalKind.Bool)
        {
            int bitmapBytes = (int)(((long)length + 7) / 8);
            if (bitmapBytes == 0)
            {
                return new ValueWriter(
                    CanonicalKind.Bool, 0, length, default, default, 0, 0, 0,
                    VortexBuffer.Empty, default);
            }

            VortexBuffer bits = CompressedValues.Allocate(
                ctx, bitmapBytes, 8, encodingId, out Span<byte> bitSpan);
            return new ValueWriter(
                CanonicalKind.Bool, 0, length, default, default, 0, 0, 0, bits, bitSpan);
        }

        int total = ArrayDecodeContext.CheckedMultiply(length, template.Width, "Decoded values");
        if (total == 0)
        {
            return new ValueWriter(
                template.Kind, template.Width, length, template.PType, template.Storage,
                template.Precision, template.Scale, bufferIndexShift, VortexBuffer.Empty, default);
        }

        VortexBuffer buffer = CompressedValues.Allocate(
            ctx, total, AlignmentFor(template.Width), encodingId, out Span<byte> span);
        return new ValueWriter(
            template.Kind, template.Width, length, template.PType, template.Storage,
            template.Precision, template.Scale, bufferIndexShift, buffer, span);
    }

    /// <summary>Copies row <paramref name="sourceRow"/> of <paramref name="source"/> to row
    /// <paramref name="destinationRow"/>.</summary>
    /// <param name="source">Where the row comes from; must be the same kind as this writer.</param>
    /// <param name="sourceRow">A row of <paramref name="source"/>, already bounds-checked.</param>
    /// <param name="destinationRow">A row of this writer, already bounds-checked.</param>
    public readonly void Copy(in ValueReader source, int sourceRow, int destinationRow)
    {
        if (_kind == CanonicalKind.Bool)
        {
            // Both branches, not just the set one: sparse fills the whole bitmap with its fill
            // value first, so a false patch over a true fill has to clear.
            if (ReadBit(source.Bytes, source.BitOffset + sourceRow))
            {
                SetBit(destinationRow);
            }
            else
            {
                ClearBit(destinationRow);
            }

            return;
        }

        Span<byte> destination = _bytes.Slice(destinationRow * _width, _width);
        source.Bytes.Slice(sourceRow * _width, _width).CopyTo(destination);
        ShiftViewBuffer(destination);
    }

    /// <summary>Writes <paramref name="count"/> copies of one source row.</summary>
    /// <param name="source">Where the row comes from.</param>
    /// <param name="sourceRow">A row of <paramref name="source"/>, already bounds-checked.</param>
    /// <param name="destinationRow">First destination row.</param>
    /// <param name="count">How many rows to write.</param>
    public readonly void Repeat(in ValueReader source, int sourceRow, int destinationRow, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (_kind == CanonicalKind.Bool)
        {
            bool value = ReadBit(source.Bytes, source.BitOffset + sourceRow);
            for (int i = 0; i < count; i++)
            {
                if (value)
                {
                    SetBit(destinationRow + i);
                }
                else
                {
                    ClearBit(destinationRow + i);
                }
            }

            return;
        }

        Copy(in source, sourceRow, destinationRow);
        ReadOnlySpan<byte> first = _bytes.Slice(destinationRow * _width, _width);
        for (int i = 1; i < count; i++)
        {
            first.CopyTo(_bytes.Slice((destinationRow + i) * _width, _width));
        }
    }

    /// <summary>Writes one row of raw bytes, already in the output's representation.</summary>
    /// <param name="row">The destination row.</param>
    /// <param name="value">Exactly <see cref="Width"/> bytes.</param>
    public readonly void WriteRow(int row, ReadOnlySpan<byte> value) =>
        value.CopyTo(_bytes.Slice(row * _width, _width));

    /// <summary>
    /// Zeroes one row. A null row's value bytes are unspecified, and zeroing is what makes a
    /// VarBinView null row an <c>empty_view()</c> rather than a copied view that might name a
    /// buffer this node does not carry.
    /// </summary>
    /// <param name="row">The destination row.</param>
    public readonly void ClearRow(int row)
    {
        if (_kind == CanonicalKind.Bool)
        {
            ClearBit(row);
            return;
        }

        _bytes.Slice(row * _width, _width).Clear();
    }

    /// <summary>Sets the bit for a Bool output row.</summary>
    /// <param name="row">The destination row.</param>
    public readonly void SetBit(int row) => _bytes[row >> 3] |= (byte)(1 << (row & 7));

    /// <summary>Clears the bit for a Bool output row.</summary>
    /// <param name="row">The destination row.</param>
    public readonly void ClearBit(int row) => _bytes[row >> 3] &= (byte)~(1 << (row & 7));

    /// <summary>Publishes the node into the canonical arena.</summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="dtype">The dtype the node must produce.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="dataBuffers">
    /// The VarBinView data buffers, in the order the written views index them. Ignored for the
    /// other kinds.
    /// </param>
    /// <returns>The new canonical node's index.</returns>
    public readonly int Complete(
        ArrayDecodeContext ctx, DType dtype, Validity validity, ReadOnlySpan<VortexBuffer> dataBuffers)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return _kind switch
        {
            CanonicalKind.Bool => ctx.Canonical.AddBool(dtype, _length, validity, _buffer, 0),
            CanonicalKind.Primitive => ctx.Canonical.AddPrimitive(dtype, _length, validity, _ptype, _buffer),
            CanonicalKind.Decimal => ctx.Canonical.AddDecimal(
                dtype, _length, validity, _storage, _precision, _scale, _buffer),
            _ => ctx.Canonical.AddVarBinView(dtype, _length, validity, _buffer, dataBuffers),
        };
    }

    private readonly void ShiftViewBuffer(Span<byte> view)
    {
        if (_bufferIndexShift == 0 || _kind != CanonicalKind.VarBinView)
        {
            return;
        }

        // Arrow BinaryView: [0..4) length, and when it exceeds 12 bytes [8..12) is the data buffer
        // index. An inline view has no buffer index to shift.
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (length <= 12)
        {
            return;
        }

        uint bufferIndex = BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            view.Slice(8, 4), bufferIndex + (uint)_bufferIndexShift);
    }

    private static bool ReadBit(ReadOnlySpan<byte> bits, int bit) =>
        (bits[bit >> 3] & (1 << (bit & 7))) != 0;

    // A views buffer must be 16-byte aligned (contract §8.4); everything else needs its own width,
    // and the pool never hands back less than that.
    private static int AlignmentFor(int width) => width switch
    {
        1 => 1,
        2 => 2,
        4 => 4,
        8 => 8,
        16 => 16,
        _ => 32,
    };
}

/// <summary>
/// Builds an output validity bitmap and collapses it, so a decoder never has to repeat contract
/// §2.6 rule 3 by hand.
/// </summary>
internal ref struct ValidityWriter
{
    private readonly int _length;
    private readonly bool _tracked;
    private readonly VortexBuffer _buffer;
    private readonly Span<byte> _bits;

    private ValidityWriter(int length, bool tracked, VortexBuffer buffer, Span<byte> bits)
    {
        _length = length;
        _tracked = tracked;
        _buffer = buffer;
        _bits = bits;
    }

    /// <summary><see langword="true"/> when a per-row bitmap is being built.</summary>
    public readonly bool IsTracked => _tracked;

    /// <summary>
    /// Creates a writer. When <paramref name="tracked"/> is false nothing is allocated and
    /// <see cref="Complete"/> returns the dtype's own validity.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="length">Row count.</param>
    /// <param name="tracked">Whether any row can be null.</param>
    /// <param name="encodingId">The encoding asking, for the error message.</param>
    public static ValidityWriter Create(
        ArrayDecodeContext ctx, int length, bool tracked, string encodingId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!tracked || length == 0)
        {
            return new ValidityWriter(length, false, VortexBuffer.Empty, default);
        }

        int bytes = (int)(((long)length + 7) / 8);
        VortexBuffer buffer = CompressedValues.Allocate(ctx, bytes, 8, encodingId, out Span<byte> span);
        return new ValidityWriter(length, true, buffer, span);
    }

    /// <summary>Marks one row valid. The bitmap starts all-invalid.</summary>
    /// <param name="row">The row.</param>
    public readonly void SetValid(int row) => _bits[row >> 3] |= (byte)(1 << (row & 7));

    /// <summary>Marks one row null, undoing an earlier <see cref="SetValid"/>.</summary>
    /// <param name="row">The row.</param>
    public readonly void SetInvalid(int row) => _bits[row >> 3] &= (byte)~(1 << (row & 7));

    /// <summary>Marks a run of rows valid.</summary>
    /// <param name="row">First row.</param>
    /// <param name="count">How many.</param>
    public readonly void SetValidRange(int row, int count)
    {
        for (int i = 0; i < count; i++)
        {
            SetValid(row + i);
        }
    }

    /// <summary>Collapses the bitmap per contract §2.6 rule 3 and publishes it if it survives.</summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="dtype">The dtype the parent node produces; its nullability answers the untracked case.</param>
    /// <param name="encodingId">The encoding asking, for the error message.</param>
    /// <returns>The validity to attach to the output node.</returns>
    /// <exception cref="VortexFormatException">
    /// A null landed in a non-nullable array - the file contradicts its own dtype.
    /// </exception>
    public readonly Validity Complete(ArrayDecodeContext ctx, DType dtype, string encodingId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!_tracked || _length == 0)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        ValidityBitmapShape shape = ArrayDecodeContext.ClassifyValidityBits(_bits, 0, _length);
        if (shape == ValidityBitmapShape.AllSet)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        if (dtype.Nullability != Nullability.Nullable)
        {
            CompressedThrow.Format(
                $"{encodingId} decoded a null row into the non-nullable dtype {dtype}.");
        }

        if (shape == ValidityBitmapShape.AllClear)
        {
            return Validity.AllInvalid;
        }

        return Validity.Bitmap(
            ctx.Canonical.AddBool(ctx.Types.Bool(Nullability.NonNullable), _length, Validity.NonNullable, _buffer, 0));
    }
}

/// <summary>Reads a decoded <see cref="Validity"/> row by row, without re-dispatching per row.</summary>
internal readonly ref struct ValidityReader
{
    private readonly ReadOnlySpan<byte> _bits;
    private readonly int _bitOffset;
    private readonly byte _mode;   // 0 = all valid, 1 = all invalid, 2 = bitmap

    private ValidityReader(ReadOnlySpan<byte> bits, int bitOffset, byte mode)
    {
        _bits = bits;
        _bitOffset = bitOffset;
        _mode = mode;
    }

    /// <summary>Resolves <paramref name="validity"/> against the arena holding its bitmap.</summary>
    /// <param name="arena">The canonical arena.</param>
    /// <param name="validity">The validity to read.</param>
    /// <exception cref="VortexFormatException">The bitmap node is not a Bool.</exception>
    public static ValidityReader Of(CanonicalArena arena, Validity validity)
    {
        ArgumentNullException.ThrowIfNull(arena);
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return new ValidityReader(default, 0, 0);
            case ValidityKind.AllInvalid:
                return new ValidityReader(default, 0, 1);
            default:
            {
                CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
                if (bits.Kind != CanonicalKind.Bool)
                {
                    CompressedThrow.Format($"A validity bitmap decoded to {bits.Kind}, not Bool.");
                }

                return new ValidityReader(bits.Bits.Span, bits.BitOffset, 2);
            }
        }
    }

    /// <summary>Every row is valid.</summary>
    public bool IsAllValid => _mode == 0;

    /// <summary>Every row is null.</summary>
    public bool IsAllInvalid => _mode == 1;

    /// <summary>Whether row <paramref name="row"/> holds a value.</summary>
    /// <param name="row">The row; the caller has bounds-checked it against the node's length.</param>
    public bool IsValid(int row) => _mode switch
    {
        0 => true,
        1 => false,
        _ => (_bits[(_bitOffset + row) >> 3] & (1 << ((_bitOffset + row) & 7))) != 0,
    };
}

/// <summary>
/// The VarBinView data buffers an expanded node must carry, optionally with one buffer of the
/// caller's own in front. Rented from <see cref="ArrayPool{T}"/> and returned by
/// <see cref="Dispose"/>, so nothing is allocated per batch.
/// </summary>
internal ref struct DataBufferSet
{
    private VortexBuffer[]? _rented;
    private int _count;

    /// <summary>The buffers, in the order the written views index them.</summary>
    public readonly ReadOnlySpan<VortexBuffer> Buffers =>
        _rented is null ? default : _rented.AsSpan(0, _count);

    /// <summary>How many buffers were prepended; the shift a copied view's index needs.</summary>
    public int LeadingCount { get; private set; }

    /// <summary>Collects the data buffers of a VarBinView source.</summary>
    /// <param name="arena">The arena holding the source.</param>
    /// <param name="source">The source node; a non-VarBinView source yields an empty set.</param>
    /// <param name="hasLeading">Whether to prepend <paramref name="leading"/>.</param>
    /// <param name="leading">The caller's own data buffer, placed at index 0.</param>
    public static DataBufferSet Collect(
        CanonicalArena arena, in ValueReader source, bool hasLeading, VortexBuffer leading)
    {
        ArgumentNullException.ThrowIfNull(arena);

        DataBufferSet set = default;
        if (source.Kind != CanonicalKind.VarBinView)
        {
            return set;
        }

        int leadingCount = hasLeading ? 1 : 0;
        int total = source.DataBufferCount + leadingCount;
        set._rented = ArrayPool<VortexBuffer>.Shared.Rent(Math.Max(total, 1));
        set._count = total;
        set.LeadingCount = leadingCount;

        CanonicalNode node = arena.GetNode(source.NodeIndex);
        if (hasLeading)
        {
            set._rented[0] = leading;
        }

        for (int i = 0; i < source.DataBufferCount; i++)
        {
            set._rented[leadingCount + i] = node.GetDataBuffer(i);
        }

        return set;
    }

    /// <summary>Returns the rented array.</summary>
    public void Dispose()
    {
        VortexBuffer[]? rented = _rented;
        _rented = null;
        _count = 0;
        if (rented is not null)
        {
            ArrayPool<VortexBuffer>.Shared.Return(rented);
        }
    }
}

/// <summary>Small helpers every compressed decoder needs and none of them may re-invent.</summary>
internal static class CompressedValues
{
    /// <summary>Allocate without the zero-fill, for a decoder that writes every byte.</summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="byteLength">Size in bytes.</param>
    /// <param name="alignment">A power of two.</param>
    /// <param name="encodingId">The encoding, for the error message.</param>
    /// <param name="destination">The writable block, NOT zeroed.</param>
    /// <returns>A non-owning view over the same bytes.</returns>
    /// <exception cref="VortexFormatException">The buffer would exceed the ceiling.</exception>
    /// <remarks>
    /// Only for a decoder that provably writes every byte; see
    /// <see cref="CanonicalArena.AllocateUninitialized"/> for why that bar is where it is.
    /// </remarks>
    public static VortexBuffer AllocateUninitialized(
        ArrayDecodeContext ctx, int byteLength, int alignment, string encodingId,
        out Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        long ceiling = ctx.Options.MaxDecompressedSize;
        if (byteLength > ceiling)
        {
            destination = default;
            CompressedThrow.Format(
                $"Decoding {encodingId} would materialize {byteLength} bytes, above the " +
                $"{ceiling}-byte decompression ceiling.");
        }

        return ctx.Canonical.AllocateUninitialized(byteLength, alignment, out destination);
    }
    /// <summary>
    /// Materializes a decoded buffer, refusing one larger than
    /// <see cref="VortexReadOptions.MaxDecompressedSize"/>.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="byteLength">Bytes to allocate; already checked for 32-bit overflow.</param>
    /// <param name="alignment">A power of two in <c>[1, VortexLimits.MaxAlignment]</c>.</param>
    /// <param name="encodingId">The encoding asking, for the error message.</param>
    /// <param name="destination">The writable, zeroed block.</param>
    /// <returns>A non-owning view over the block.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the amplification guard, and every allocation in this component goes through
    /// it.</b> Several compressed encodings produce output out of proportion to their input:
    /// <c>vortex.sequence</c> has no children and no buffers at all, <c>fastlanes.bitpacked</c> at
    /// bit width 0 has an empty packed buffer, and <c>vortex.sparse</c> needs only a fill scalar.
    /// For each of them a few hundred bytes of file can declare 2^31 rows, so without a ceiling a
    /// 1 KB file is a multi-gigabyte allocation - the "unbounded allocation" the threat model of
    /// docs/09-contracts.md §4 rules out. The row count itself is validated much further out, at
    /// the layout level, and nothing between there and here bounds the product.
    /// </para>
    /// <para>
    /// The ceiling is docs/08-semantics.md §6's <c>MaxDecompressedSize</c>, applied per decoded
    /// node's output buffer - never a constant invented at the call site (contract §1.5). A caller
    /// with a genuinely larger column raises <see cref="VortexReadOptions.MaxDecompressedSize"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="VortexFormatException">The buffer would exceed the ceiling.</exception>
    public static VortexBuffer Allocate(
        ArrayDecodeContext ctx, int byteLength, int alignment, string encodingId,
        out Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        long ceiling = ctx.Options.MaxDecompressedSize;
        if (byteLength > ceiling)
        {
            destination = default;
            CompressedThrow.Format(
                $"Decoding {encodingId} would materialize {byteLength} bytes, above the " +
                $"{ceiling}-byte decompression ceiling.");
        }

        return ctx.Canonical.Allocate(byteLength, alignment, out destination);
    }

    /// <summary>
    /// Reads element <paramref name="index"/> of an unsigned integer span as a <see cref="ulong"/>.
    /// </summary>
    /// <param name="bytes">The values, little-endian.</param>
    /// <param name="ptype">The element's physical type; must be an unsigned integer.</param>
    /// <param name="index">The element, already bounds-checked.</param>
    public static ulong ReadUnsigned(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.U8 => bytes[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(index * 8, 8)),
    };

    /// <summary>
    /// Reads element <paramref name="index"/> of a signed or unsigned integer span as a
    /// <see cref="long"/>. Unsigned values above <see cref="long.MaxValue"/> saturate, which is
    /// safe because every caller compares the result against a non-negative bound.
    /// </summary>
    /// <param name="bytes">The values, little-endian.</param>
    /// <param name="ptype">The element's physical type; must be an integer.</param>
    /// <param name="index">The element, already bounds-checked.</param>
    public static long ReadInteger(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.U8 => bytes[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.U64 => Saturate(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(index * 8, 8))),
        PType.I8 => (sbyte)bytes[index],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
    };

    private static long Saturate(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>
    /// Narrows a decoded validity to the window <c>[offset, offset + length)</c>. Used where an
    /// encoding's validity lives on a child that is longer than the array itself -
    /// <c>fastlanes.rle</c>, whose indices child is padded out to a multiple of 1024.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="validity">The child's validity.</param>
    /// <param name="offset">First row of the window, within the child.</param>
    /// <param name="length">Rows in the window.</param>
    /// <param name="dtype">The dtype the window's node produces.</param>
    /// <returns>The windowed validity, collapsed per contract §2.6 rule 3.</returns>
    /// <remarks>
    /// The bitmap is re-based by moving the byte pointer and keeping the residual bit offset, so
    /// nothing is copied: shifting the bits would be an allocation and a copy per batch, and
    /// <see cref="CanonicalArena.AddBool"/> carries the offset for exactly this reason.
    /// </remarks>
    /// <exception cref="VortexFormatException">The window escapes the bitmap.</exception>
    public static Validity SliceValidity(
        ArrayDecodeContext ctx, Validity validity, int offset, int length, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (validity.Kind != ValidityKind.Bitmap)
        {
            return validity.Kind == ValidityKind.AllInvalid
                ? Validity.AllInvalid
                : Validity.FromNullability(dtype.Nullability);
        }

        if (length == 0)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        CanonicalNode bits = ctx.Canonical.GetNode(validity.CanonicalNodeIndex);
        if (bits.Kind != CanonicalKind.Bool)
        {
            CompressedThrow.Format($"A validity bitmap decoded to {bits.Kind}, not Bool.");
        }

        long startBit = (long)bits.BitOffset + offset;
        int byteShift = (int)(startBit >> 3);
        int bitOffset = (int)(startBit & 7);
        VortexBuffer window = bits.Bits.Slice(byteShift);

        ValidityBitmapShape shape =
            ArrayDecodeContext.ClassifyValidityBits(window.Span, bitOffset, length);
        if (shape == ValidityBitmapShape.AllSet)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        if (dtype.Nullability != Nullability.Nullable)
        {
            CompressedThrow.Format($"A null row landed in the non-nullable dtype {dtype}.");
        }

        if (shape == ValidityBitmapShape.AllClear)
        {
            return Validity.AllInvalid;
        }

        return Validity.Bitmap(ctx.Canonical.AddBool(
            ctx.Types.Bool(Nullability.NonNullable), length, Validity.NonNullable, window, bitOffset));
    }

    /// <summary>The unsigned counterpart of a signed integer physical type.</summary>
    /// <param name="ptype">A signed or unsigned integer physical type.</param>
    /// <exception cref="VortexFormatException"><paramref name="ptype"/> is not an integer.</exception>
    public static PType ToUnsigned(PType ptype) => ptype switch
    {
        PType.I8 or PType.U8 => PType.U8,
        PType.I16 or PType.U16 => PType.U16,
        PType.I32 or PType.U32 => PType.U32,
        PType.I64 or PType.U64 => PType.U64,
        _ => CompressedThrow.Format<PType>($"{ptype} has no unsigned counterpart."),
    };

    /// <summary>
    /// Requires that <paramref name="dtype"/> is an integer <c>Primitive</c>, and returns its
    /// physical type.
    /// </summary>
    /// <param name="dtype">The inherited dtype.</param>
    /// <param name="encodingId">The encoding asking, for the error message.</param>
    /// <exception cref="VortexFormatException">The dtype is not an integer primitive.</exception>
    public static PType RequireIntegerPrimitive(DType dtype, string encodingId)
    {
        if (dtype.Kind != DTypeKind.Primitive || !dtype.PType.IsInteger())
        {
            CompressedThrow.Format($"{encodingId} requires an integer primitive dtype, not {dtype}.");
        }

        return dtype.PType;
    }

    /// <summary>
    /// Requires that a decoded child is a <c>Primitive</c> of exactly
    /// <paramref name="ptype"/> and <paramref name="length"/> rows with no nulls, and returns its
    /// bytes. Used for every index-like child: patch indices, dict codes, run ends, RLE offsets.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="nodeIndex">The decoded child.</param>
    /// <param name="ptype">The physical type the metadata declared.</param>
    /// <param name="length">The row count the metadata declared.</param>
    /// <param name="encodingId">The encoding asking.</param>
    /// <param name="what">The child's role, for the error message.</param>
    /// <exception cref="VortexFormatException">Any of the three requirements fails.</exception>
    public static ReadOnlySpan<byte> RequireIndexChild(
        ArrayDecodeContext ctx, int nodeIndex, PType ptype, int length, string encodingId, string what)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        CanonicalNode node = ctx.Canonical.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(encodingId, what, node.Kind, "a Primitive");
        }

        if (node.PType != ptype)
        {
            CompressedThrow.Format(
                $"{encodingId}'s {what} child decoded as {node.PType.Name()}; " +
                $"{ptype.Name()} was declared.");
        }

        if (node.Length != length)
        {
            CompressedThrow.ChildLength(encodingId, what, node.Length, length);
        }

        if (!node.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{encodingId}'s {what} child must not contain nulls.");
        }

        return node.Values.Span;
    }
}
