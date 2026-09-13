// Phase 1 contract §12.1 and docs/07-dotnet-mapping.md - the public face of the library.
//
// THE LIFETIME CONTRACT, restated at every entry point below because it is the sharp edge of the
// whole design (docs/07-dotnet-mapping.md §4): every span borrowed from a batch is invalid after
// that batch is disposed. The column views are `readonly ref struct`s so the compiler rejects most
// of the ways a caller would outlive the batch; the remaining hole - reaching through a stale view
// after Dispose - is closed here by a disposed flag every accessor checks, so the failure is an
// ObjectDisposedException rather than a read of recycled pool memory.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>
/// One decoded batch of rows: the root canonical array, plus the schema and row offset that place
/// it in the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Every span this batch hands out - a primitive's values, a bool bitmap, a
/// string's UTF-8 bytes - points into memory the batch's scan owns. Disposing the batch releases
/// that memory and every borrowed span becomes invalid. Callers who need a value to outlive the
/// batch copy it explicitly (<see cref="BinaryColumn.GetString"/> is the one accessor that copies
/// for you).
/// </para>
/// <para>
/// <b>Affinity.</b> A batch is affine to a single consumer and is not thread-safe
/// (docs/09-contracts.md §1). Disposing it resets the scan's arenas, so a batch cannot outlive its
/// successor: a caller that wants two batches alive at once must copy.
/// </para>
/// </remarks>
public sealed class RecordBatch : IDisposable
{
    private readonly ScanContext? _context;
    private readonly CanonicalArena _arena;
    private readonly int _root;
    private readonly DType _schema;
    private readonly int _rowCount;
    private readonly long _startRow;
    private readonly int _fieldCount;
    private readonly bool _isTabular;

    // Allocated on the first NullCount() of a Bitmap column and never before: a scan that does not
    // ask for null counts stays allocation-free per batch (docs/03-architecture.md §4 invariant 1).
    private int[]? _nullCounts;
    private bool _disposed;

    /// <summary>
    /// Wraps the canonical root a scan just decoded. Disposing the batch calls
    /// <see cref="ScanContext.ResetBatch"/>, which releases the batch's segments and resets its
    /// arenas.
    /// </summary>
    /// <param name="context">The scan context that produced the root.</param>
    /// <param name="rootCanonicalIndex">The root's index in <see cref="ScanContext.Canonical"/>.</param>
    /// <param name="startRow">The absolute file row index of row 0 of this batch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startRow"/> is negative.</exception>
    /// <exception cref="VortexFormatException">
    /// The root index is not in the arena, or the root is a struct whose field count disagrees with
    /// its own dtype.
    /// </exception>
    public RecordBatch(ScanContext context, int rootCanonicalIndex, long startRow)
        : this(ArenaOf(context), rootCanonicalIndex, startRow, context)
    {
    }

    /// <summary>
    /// Wraps a canonical root in an arena the caller owns. Disposing the batch invalidates the
    /// batch but leaves the arena alone; the caller stays responsible for resetting it.
    /// </summary>
    /// <param name="arena">The arena holding the root and all of its descendants.</param>
    /// <param name="rootCanonicalIndex">The root's index in <paramref name="arena"/>.</param>
    /// <param name="startRow">The absolute file row index of row 0 of this batch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startRow"/> is negative.</exception>
    /// <exception cref="VortexFormatException">
    /// The root index is not in the arena, or the root is a struct whose field count disagrees with
    /// its own dtype.
    /// </exception>
    public RecordBatch(CanonicalArena arena, int rootCanonicalIndex, long startRow)
        : this(arena, rootCanonicalIndex, startRow, null)
    {
    }

    private RecordBatch(CanonicalArena arena, int rootCanonicalIndex, long startRow, ScanContext? context)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentOutOfRangeException.ThrowIfNegative(startRow);

        _arena = arena;
        _context = context;
        _root = rootCanonicalIndex;

        // GetNode bounds-checks and throws VortexFormatException for an index outside the arena.
        CanonicalNode root = arena.GetNode(rootCanonicalIndex);
        _schema = root.DType;
        _rowCount = root.Length;
        _startRow = startRow;

        // docs/07-dotnet-mapping.md §5: a non-struct root is exposed as itself, not faked into a
        // one-column table with an invented name.
        _isTabular = !_schema.IsDefault && _schema.Kind == DTypeKind.Struct;
        _fieldCount = _isTabular ? _schema.FieldCount : 1;

        if (_isTabular && root.Kind == CanonicalKind.Struct && root.FieldCount != _fieldCount)
        {
            ColumnsThrow.Format(
                $"The batch's root struct holds {root.FieldCount} decoded fields but its dtype " +
                $"declares {_fieldCount}.");
        }
    }

    /// <summary>
    /// The dtype of this batch's root. For a projected scan it is the projected schema, not the
    /// file's. May be any dtype, including a non-struct one (docs/07-dotnet-mapping.md §5).
    /// </summary>
    public DType Schema
    {
        get
        {
            ThrowIfDisposed();
            return _schema;
        }
    }

    /// <summary>Rows in this batch.</summary>
    public int RowCount
    {
        get
        {
            ThrowIfDisposed();
            return _rowCount;
        }
    }

    /// <summary>The absolute row index, within the file, of row 0 of this batch.</summary>
    public long StartRow
    {
        get
        {
            ThrowIfDisposed();
            return _startRow;
        }
    }

    /// <summary><see langword="true"/> when <see cref="Schema"/> is a struct.</summary>
    public bool IsTabular
    {
        get
        {
            ThrowIfDisposed();
            return _isTabular;
        }
    }

    /// <summary>
    /// Number of columns: the struct root's field count, or 1 for a non-struct root
    /// (docs/07-dotnet-mapping.md §5).
    /// </summary>
    public int FieldCount
    {
        get
        {
            ThrowIfDisposed();
            return _fieldCount;
        }
    }

    /// <summary>
    /// The root column. For a struct root this is the struct itself; for a non-struct root it is
    /// the single column the file holds.
    /// </summary>
    /// <remarks>Borrowed spans are invalid after <see cref="Dispose"/>.</remarks>
    public VortexColumn Root
    {
        get
        {
            ThrowIfDisposed();
            return new VortexColumn(this, _root);
        }
    }

    /// <summary>
    /// The name of column <paramref name="index"/>, or <see langword="null"/> for a non-struct
    /// root - we do not invent a synthetic name, because a round trip through our own writer would
    /// then produce a different schema than the input (docs/07-dotnet-mapping.md §5).
    /// </summary>
    /// <param name="index">0-based column index, below <see cref="FieldCount"/>.</param>
    /// <returns>The field name, or <see langword="null"/> when the root is not a struct.</returns>
    /// <remarks>ALLOCATES a string. <see cref="StructColumn.GetFieldNameUtf8"/> does not.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public string? GetFieldName(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)_fieldCount)
        {
            ColumnsThrow.FieldIndex(index, _fieldCount);
        }

        return _isTabular ? _schema.GetFieldName(index) : null;
    }

    /// <summary>Resolves a column by its UTF-8 name. Always false for a non-struct root.</summary>
    /// <param name="nameUtf8">The field name, UTF-8. A field name may be empty or contain a
    /// <c>'.'</c>; this lookup is exact and does no path splitting.</param>
    /// <param name="index">The 0-based column index when found.</param>
    /// <returns><see langword="true"/> when the schema has a field with that exact name.</returns>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public bool TryGetFieldIndex(ReadOnlySpan<byte> nameUtf8, out int index)
    {
        ThrowIfDisposed();
        if (!_isTabular)
        {
            index = -1;
            return false;
        }

        int found = _schema.IndexOfField(nameUtf8);
        index = found;
        return found >= 0;
    }

    /// <summary>Column <paramref name="index"/>.</summary>
    /// <param name="index">0-based, below <see cref="FieldCount"/>. For a non-struct root the only
    /// legal index is 0, and it returns <see cref="Root"/>.</param>
    /// <returns>An untyped view; downcast it with the <c>As*</c> methods.</returns>
    /// <remarks>Borrowed spans are invalid after <see cref="Dispose"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">The root's dtype says struct but its decoded form
    /// does not.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public VortexColumn Column(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)_fieldCount)
        {
            ColumnsThrow.FieldIndex(index, _fieldCount);
        }

        if (!_isTabular)
        {
            return new VortexColumn(this, _root);
        }

        // GetFieldIndex throws VortexFormatException when the root decoded to something other than
        // a struct - a file that promised a struct and delivered otherwise.
        return new VortexColumn(this, _arena.GetNode(_root).GetFieldIndex(index));
    }

    /// <summary>Column <paramref name="nameUtf8"/>.</summary>
    /// <param name="nameUtf8">The exact field name, UTF-8.</param>
    /// <returns>An untyped view; downcast it with the <c>As*</c> methods.</returns>
    /// <remarks>Borrowed spans are invalid after <see cref="Dispose"/>.</remarks>
    /// <exception cref="ArgumentException">The schema has no field with that name.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public VortexColumn Column(ReadOnlySpan<byte> nameUtf8)
    {
        if (!TryGetFieldIndex(nameUtf8, out int index))
        {
            ColumnsThrow.UnknownField(nameUtf8);
        }

        return Column(index);
    }

    /// <summary>
    /// Releases the batch. Every span borrowed from it is invalid afterwards, and every member
    /// throws <see cref="ObjectDisposedException"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// When the batch was built over a <see cref="ScanContext"/> this calls
    /// <see cref="ScanContext.ResetBatch"/>: the batch's segments are released and its arenas are
    /// reset for the next batch. That is why a batch cannot outlive its successor.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nullCounts = null;
        _context?.ResetBatch();
    }

    /// <summary><see langword="true"/> once <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The arena every column view indexes into.</summary>
    /// <remarks>
    /// Public so a consumer outside this assembly - the writer, the row encoder - can address
    /// nodes by index rather than through the <c>ref struct</c> column views, which cannot be
    /// stored. It hands out no capability the <see cref="CanonicalArena"/> type does not already
    /// expose, but it is bound by the same lifetime as every other batch-borrowed view: the arena
    /// is reset when this batch is disposed.
    /// </remarks>
    public CanonicalArena Arena
    {
        get
        {
            ThrowIfDisposed();
            return _arena;
        }
    }

    /// <summary>The root node's index in <see cref="Arena"/>.</summary>
    public int RootIndex
    {
        get
        {
            ThrowIfDisposed();
            return _root;
        }
    }

    /// <summary>Fetches a canonical node, refusing to touch the arena after disposal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CanonicalNode Node(int index)
    {
        ThrowIfDisposed();
        return _arena.GetNode(index);
    }

    /// <summary>
    /// The number of null rows of the node at <paramref name="nodeIndex"/>. O(1) for every validity
    /// kind but <see cref="ValidityKind.Bitmap"/>, which is counted once and cached.
    /// </summary>
    internal int NullCount(int nodeIndex)
    {
        CanonicalNode node = Node(nodeIndex);
        Validity validity = node.Validity;

        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return 0;
            case ValidityKind.AllInvalid:
                return node.Length;
            default:
                break;
        }

        int[] cache = _nullCounts ??= NewNullCountCache();
        bool cacheable = (uint)nodeIndex < (uint)cache.Length;
        if (cacheable && cache[nodeIndex] >= 0)
        {
            return cache[nodeIndex];
        }

        CanonicalNode bits = Node(validity.CanonicalNodeIndex);
        int nulls = CountClearBits(bits.Bits.Span, bits.BitOffset, node.Length);
        if (cacheable)
        {
            cache[nodeIndex] = nulls;
        }

        return nulls;
    }

    private int[] NewNullCountCache()
    {
        int[] cache = new int[Math.Max(_arena.NodeCount, 1)];
        cache.AsSpan().Fill(-1);
        return cache;
    }

    /// <summary>
    /// Counts the clear bits of <paramref name="length"/> bits starting at
    /// <paramref name="bitOffset"/>, LSB-first within each byte.
    /// </summary>
    internal static int CountClearBits(ReadOnlySpan<byte> bits, int bitOffset, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        long endBit = (long)bitOffset + length;
        long neededBytes = (endBit + 7) / 8;
        if (neededBytes > bits.Length)
        {
            // The arena's AddBool already refuses a short bitmap; re-checking here costs one
            // compare per column and keeps a decoder bug from becoming an out-of-bounds read.
            ColumnsThrow.Format(
                $"A validity bitmap of {bits.Length} bytes cannot describe {length} rows at bit " +
                $"offset {bitOffset}.");
        }

        // Counted eight bytes at a time, with the two partial ends masked once rather than tested
        // per byte -- PERF-AUDIT §4.2 asks for every bit-at-a-time site to arrive at this one
        // kernel, and this loop carried an `i == firstByte` and an `i == lastByte` compare through
        // all 125 000 iterations of a million-row bitmap.
        return length - Arrays.Decoders.Canonical.BitmapKernels.CountSet(bits, bitOffset, length);
    }

    /// <summary>
    /// Reads bit <paramref name="bitIndex"/> of an LSB-first bitmap, bounds-checked against the
    /// span rather than the caller's arithmetic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool GetBit(ReadOnlySpan<byte> bits, int bitIndex)
    {
        int byteIndex = bitIndex >> 3;
        if ((uint)byteIndex >= (uint)bits.Length)
        {
            ColumnsThrow.Format(
                $"Bit {bitIndex} is outside a {bits.Length}-byte bitmap.");
        }

        return ((bits[byteIndex] >> (bitIndex & 7)) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            ThrowDisposed();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private void ThrowDisposed() =>
        throw new ObjectDisposedException(
            nameof(RecordBatch),
            "This RecordBatch has been disposed; every span borrowed from it is invalid " +
            "(docs/07-dotnet-mapping.md §4).");

    private static CanonicalArena ArenaOf(ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Canonical;
    }
}
