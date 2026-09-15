// Phase 1 contract §12.2. Every column view is a `readonly ref struct`: that is the enforcement
// mechanism for the lifetime contract, because a ref struct cannot be stored in a field, boxed,
// captured by a lambda, or carried across an `await`, so the compiler rejects most of the ways a
// caller would outlive the batch (docs/07-dotnet-mapping.md §4).
//
// Nullability is orthogonal and never in the .NET type (docs/07-dotnet-mapping.md §1): a nullable
// i32 column is a PrimitiveColumn<int>, not a PrimitiveColumn<int?>. Mapping to T? would allocate
// and destroy the span contract, so nulls live entirely in ValidityKind / IsValid.
using System;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>
/// An untyped view over one decoded column. Downcast it with the <c>As*</c> methods, which validate
/// the dtype.
/// </summary>
/// <remarks>
/// Every span reachable from this view is borrowed from the owning
/// <see cref="RecordBatch"/> and is invalid once that batch is disposed
/// (docs/07-dotnet-mapping.md §4).
/// </remarks>
public readonly ref struct VortexColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal VortexColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>The column's logical type.</summary>
    public DType DType => _batch.Node(_node).DType;

    /// <summary>Rows in this column.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>
    /// How this column represents nulls. Exposed so callers can skip per-row checks on the common
    /// cases (docs/07-dotnet-mapping.md §1).
    /// </summary>
    public ValidityKind ValidityKind => _batch.Node(_node).Validity.Kind;

    /// <summary>
    /// <see langword="true"/> when no row is null - <see cref="Arrays.ValidityKind.NonNullable"/>
    /// or <see cref="Arrays.ValidityKind.AllValid"/> - so a caller may skip
    /// <see cref="IsValid"/> entirely.
    /// </summary>
    public bool IsAllValid => _batch.Node(_node).Validity.IsAllValid;

    /// <summary>
    /// The number of null rows. O(1) except for
    /// <see cref="Arrays.ValidityKind.Bitmap"/>, which is counted once per batch and cached.
    /// </summary>
    /// <remarks>
    /// The first bitmap count on a batch allocates a small per-batch cache; nothing else in this
    /// API allocates.
    /// </remarks>
    public int NullCount => _batch.NullCount(_node);

    /// <summary><see langword="true"/> when row <paramref name="index"/> is not null. O(1).</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The owning batch has been disposed.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);

    /// <summary>The all-null column view.</summary>
    /// <exception cref="InvalidOperationException">The column is not a Null column.</exception>
    public NullColumn AsNull()
    {
        Require(CanonicalKind.Null);
        return new NullColumn(_batch, _node);
    }

    /// <summary>The boolean column view.</summary>
    /// <exception cref="InvalidOperationException">The column is not a Bool column.</exception>
    public BoolColumn AsBool()
    {
        Require(CanonicalKind.Bool);
        return new BoolColumn(_batch, _node);
    }

    /// <summary>
    /// The primitive column view. <typeparamref name="T"/> must match the column's
    /// <see cref="PType"/> exactly: <c>u8</c> is <see cref="byte"/>, <c>i8</c> is
    /// <see cref="sbyte"/>, <c>f16</c> is <see cref="Half"/>, and so on. Reinterpreting one width
    /// as another is the caller's job (Phase 1 contract §12.3).
    /// </summary>
    /// <typeparam name="T">The matching .NET element type.</typeparam>
    /// <exception cref="InvalidOperationException">
    /// The column is not primitive, or <typeparamref name="T"/> is not its exact element type.
    /// </exception>
    public PrimitiveColumn<T> AsPrimitive<T>()
        where T : unmanaged
    {
        int index = Resolve(CanonicalKind.Primitive, out CanonicalNode node);
        if (node.Kind != CanonicalKind.Primitive)
        {
            ColumnsThrow.WrongKind(node.Kind.ToString(), nameof(CanonicalKind.Primitive));
        }

        PType ptype = node.PType;
        if (!PrimitiveColumn<T>.Matches(ptype))
        {
            ColumnsThrow.WrongElementType(ptype, typeof(T));
        }

        return new PrimitiveColumn<T>(_batch, index);
    }

    /// <summary>The decimal column view.</summary>
    /// <exception cref="InvalidOperationException">The column is not a Decimal column.</exception>
    public DecimalColumn AsDecimal()
    {
        Require(CanonicalKind.Decimal);
        return new DecimalColumn(_batch, _node);
    }

    /// <summary>The binary column view; covers both <c>Utf8</c> and <c>Binary</c> dtypes.</summary>
    /// <exception cref="InvalidOperationException">The column is not a VarBinView column.</exception>
    public BinaryColumn AsBinary()
    {
        Require(CanonicalKind.VarBinView);
        return new BinaryColumn(_batch, _node);
    }

    /// <summary>The struct column view.</summary>
    /// <exception cref="InvalidOperationException">The column is not a Struct column.</exception>
    public StructColumn AsStruct()
    {
        Require(CanonicalKind.Struct);
        return new StructColumn(_batch, _node);
    }

    /// <summary>
    /// The variable-length list view. Both <c>vortex.list</c> and <c>vortex.listview</c> decode to
    /// the same canonical ListView form (Phase 1 contract §8.4).
    /// </summary>
    /// <exception cref="InvalidOperationException">The column is not a ListView column.</exception>
    public ListColumn AsList()
    {
        Require(CanonicalKind.ListView);
        return new ListColumn(_batch, _node);
    }

    /// <summary>The fixed-size list view.</summary>
    /// <exception cref="InvalidOperationException">The column is not a FixedSizeList column.</exception>
    public FixedSizeListColumn AsFixedSizeList()
    {
        Require(CanonicalKind.FixedSizeList);
        return new FixedSizeListColumn(_batch, _node);
    }

    /// <summary>The extension view: the four core temporal/uuid dtypes over a storage column.</summary>
    /// <exception cref="InvalidOperationException">The column is not an Extension column.</exception>
    public ExtensionColumn AsExtension()
    {
        Require(CanonicalKind.Extension);
        return new ExtensionColumn(_batch, _node);
    }

    /// <summary>The canonical form this column decoded to.</summary>
    public CanonicalKind Kind => _batch.Node(_node).Kind;

    /// <summary>
    /// Resolves the node this accessor should read, materializing a constant when the caller asks
    /// for a form only the expanded column can give. PERF-AUDIT-v2.md Z1b-c2b2.
    /// </summary>
    /// <param name="kind">The kind the typed accessor needs.</param>
    /// <param name="node">The resolved node.</param>
    /// <returns>The node index to hand the typed view -- the twin's, for a materialized constant.</returns>
    /// <remarks>
    /// A typed view hands out a CONTIGUOUS span, which one element and a count cannot be. So the
    /// constant form ends where a caller asks for one, and it ends ONCE: the twin is memoized on the
    /// record. Every path that never asks -- filter, take, prune, write back -- keeps the form, and
    /// those are the paths the column was tiled for.
    /// </remarks>
    private int Resolve(CanonicalKind kind, out CanonicalNode node)
    {
        node = _batch.Node(_node);
        if (node.Kind == CanonicalKind.Constant &&
            kind is CanonicalKind.Primitive or CanonicalKind.Decimal)
        {
            int twin = _batch.Arena.MaterializeConstant(_node);
            node = _batch.Node(twin);
            return twin;
        }

        return _node;
    }

    private CanonicalNode Require(CanonicalKind kind)
    {
        CanonicalNode node = _batch.Node(_node);
        if (node.Kind != kind)
        {
            // A caller asking a Utf8 column for AsPrimitive<int>() is a caller error, not a
            // malformed file (Phase 1 contract §1.4).
            ColumnsThrow.WrongKind(node.Kind.ToString(), kind.ToString());
        }

        return node;
    }
}

/// <summary>Shared row-level primitives every typed column view uses.</summary>
internal static class ColumnCore
{
    /// <summary>
    /// Row validity. <see cref="ValidityKind.NonNullable"/> and
    /// <see cref="ValidityKind.AllValid"/> answer without touching memory,
    /// <see cref="ValidityKind.AllInvalid"/> likewise; only
    /// <see cref="ValidityKind.Bitmap"/> reads a bit, and it applies the canonical Bool node's bit
    /// offset (Phase 1 contract §2.6 rule 5).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValid(RecordBatch batch, int nodeIndex, int rowIndex)
    {
        CanonicalNode node = batch.Node(nodeIndex);
        if ((uint)rowIndex >= (uint)node.Length)
        {
            ColumnsThrow.RowIndex(rowIndex, node.Length);
        }

        Validity validity = node.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return true;
            case ValidityKind.AllInvalid:
                return false;
            default:
                CanonicalNode bits = batch.Node(validity.CanonicalNodeIndex);
                return RecordBatch.GetBit(bits.Bits.Span, bits.BitOffset + rowIndex);
        }
    }

    /// <summary>Bounds-checks a caller-supplied row index against a column length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckRow(int rowIndex, int length)
    {
        if ((uint)rowIndex >= (uint)length)
        {
            ColumnsThrow.RowIndex(rowIndex, length);
        }
    }

    /// <summary>
    /// Reads element <paramref name="index"/> of a buffer of <paramref name="ptype"/> integers as a
    /// <see cref="long"/>: signed widths sign-extend, unsigned widths zero-extend, and a
    /// <c>u64</c> above <see cref="long.MaxValue"/> is <see cref="VortexFormatException"/> rather
    /// than a silently negative offset.
    /// </summary>
    /// <remarks>
    /// Used for list offsets and sizes, whose physical types the decoders read from metadata; a
    /// float ptype there is malformed, not a caller error.
    /// </remarks>
    internal static long ReadInteger(ReadOnlySpan<byte> buffer, PType ptype, int index, string what)
    {
        int width = ptype.ByteWidth();

        // 64-bit arithmetic on purpose: `index * width` and `offset + width` both overflow an int
        // for an index near int.MaxValue, and an overflowed sum compares BELOW the buffer length,
        // which would turn the guard into a pass and the slice into an out-of-bounds read.
        long offset = (long)index * width;
        if (index < 0 || offset + width > buffer.Length)
        {
            ColumnsThrow.Format(
                $"{what} element {index} needs bytes [{offset}, {offset + width}) of a " +
                $"{buffer.Length}-byte buffer.");
        }

        ReadOnlySpan<byte> slot = buffer.Slice((int)offset, width);
        switch (ptype)
        {
            case PType.U8:
                return slot[0];
            case PType.U16:
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(slot);
            case PType.U32:
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slot);
            case PType.U64:
            {
                ulong raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(slot);
                if (raw > long.MaxValue)
                {
                    ColumnsThrow.Format($"{what} element {index} is {raw}, which exceeds long.MaxValue.");
                }

                return (long)raw;
            }

            case PType.I8:
                return (sbyte)slot[0];
            case PType.I16:
                return System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(slot);
            case PType.I32:
                return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slot);
            case PType.I64:
                return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slot);
            default:
                return ColumnsThrow.Format<long>(
                    $"{what} has physical type {ptype.Name()}, which is not an integer.");
        }
    }
}
