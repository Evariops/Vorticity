using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One column of a builder: its rows in native buffers rented from the session's pool, laid out in
/// the canonical form the engine reads, so that a batch over them references the buffers rather
/// than copying them.
/// </summary>
/// <remarks>
/// A store keeps the rows the writer has accepted but not yet encoded (<see cref="Committed"/>)
/// ahead of the rows being appended. The validity is lazy: until the first null it is not
/// allocated, and after that only the rows up to the last null carry a bit, every later row being
/// valid by construction.
/// </remarks>
internal abstract unsafe class ColumnStore
{
    private static readonly DType BitmapType = new DTypeArena().Bool(Nullability.NonNullable);

    private NativeSegmentOwner? _validityOwner;
    private byte* _validity;
    private int _validityBytes;
    private int _known;
    private bool _nulls;

    protected ColumnStore(DType dtype, AlignedBufferPool pool)
        : this(dtype, VortexTypes.FromDType(dtype), pool)
    {
    }

    protected ColumnStore(DType dtype, VortexType type, AlignedBufferPool pool)
    {
        DType = dtype;
        Type = type;
        Pool = pool;
    }

    /// <summary>The engine's dtype of the nodes this store builds.</summary>
    internal DType DType { get; }

    /// <summary>The column's type.</summary>
    internal VortexType Type { get; }

    internal bool IsNullable => Type.IsNullable;

    protected AlignedBufferPool Pool { get; }

    /// <summary>Rows appended, committed or not; a struct counts its fields' rows instead.</summary>
    internal int Count;

    /// <summary>Rows the writer has accepted and not yet encoded, at the front of the buffers.</summary>
    internal int Committed;

    /// <summary>The .NET type a builder last asked this column for, once it was found to fit.</summary>
    internal Type? Checked;

    /// <summary>The store a <see cref="ColumnBuilder{T}"/> writes to: the storage of an extension, this store otherwise.</summary>
    internal virtual ColumnStore Leaf => this;

    /// <summary>The metadata of the extension this store is the storage of, for a registered type's conversion.</summary>
    internal byte[] ExtensionMetadata { get; set; } = [];

    /// <summary>The session's registered extensions, which decide what a .NET type of theirs writes.</summary>
    internal VortexExtensionRegistry? Extensions { get; set; }

    internal virtual int Rows => Count;

    /// <summary>Adds the node of the first <paramref name="rows"/> rows to <paramref name="arena"/>, over this store's buffers.</summary>
    internal abstract int Build(CanonicalArena arena, int rows);

    /// <summary>Drops the first <paramref name="rows"/> rows, which the writer has encoded.</summary>
    internal abstract void Discard(int rows);

    /// <summary>Drops every row from <paramref name="rows"/> on.</summary>
    internal abstract void Truncate(int rows);

    /// <summary>Marks every row appended as accepted by the writer.</summary>
    internal virtual void Commit() => Committed = Count;

    /// <summary>The bytes the accepted rows occupy.</summary>
    internal abstract long CommittedBytes { get; }

    /// <summary>Appends one row holding the type's default value: zero, false, empty.</summary>
    internal abstract void AppendDefault();

    /// <summary>Why the store cannot be written as it stands, or null when it can.</summary>
    internal virtual string? Inconsistency() => null;

    /// <summary>Gives every buffer back to the pool.</summary>
    internal virtual void Release()
    {
        ReleaseValidity();
        Count = 0;
        Committed = 0;
    }

    /// <summary>Appends one null row; each store knows what its null slot holds.</summary>
    internal virtual void AppendNull() => throw new VortexSchemaException($"A column of {Type} takes no null through this builder.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void RequireNullable()
    {
        if (!IsNullable)
        {
            ThrowNotNullable();
        }
    }

    [DoesNotReturn]
    private void ThrowNotNullable() =>
        throw new VortexSchemaException($"The column is {Type}, which is not nullable; a null cannot be appended to it.");

    /// <summary>Marks <paramref name="count"/> rows from <paramref name="row"/> null; the rows before them without a bit are valid.</summary>
    protected void MarkNulls(int row, int count)
    {
        EnsureValidity(row + count);
        Span<byte> bits = new Span<byte>(_validity, _validityBytes);
        BitmapKernels.SetRange(bits, _known, row - _known);
        BitmapKernels.ClearRange(bits, row, count);
        _known = row + count;
        _nulls = true;
    }

    /// <summary>
    /// Takes the validity of <paramref name="count"/> rows from <paramref name="row"/> out of
    /// <paramref name="words"/>, least significant bit first; allocates nothing when they are all valid.
    /// </summary>
    protected void CopyValidity(int row, ReadOnlySpan<ulong> words, int count)
    {
        if ((long)words.Length * 64 < count)
        {
            throw new ArgumentException($"The validity holds {words.Length * 64L} bits for {count} values.", nameof(words));
        }

        int whole = count >> 6;
        bool all = words[..whole].IndexOfAnyExcept(ulong.MaxValue) < 0;
        int tail = count & 63;
        if (all && tail != 0)
        {
            ulong mask = (1UL << tail) - 1;
            all = (words[whole] & mask) == mask;
        }

        if (all)
        {
            return;
        }

        RequireNullable();
        EnsureValidity(row + count);
        Span<byte> bits = new Span<byte>(_validity, _validityBytes);
        BitmapKernels.SetRange(bits, _known, row - _known);
        BitmapKernels.CopyRange(MemoryMarshal.AsBytes(words), 0, bits, row, count);
        _known = row + count;
        _nulls = true;
    }

    /// <summary>The validity of the first <paramref name="rows"/> rows, as a node of <paramref name="arena"/> when it has a null.</summary>
    protected Validity BuildValidity(CanonicalArena arena, int rows)
    {
        if (!_nulls)
        {
            return IsNullable ? Validity.AllValid : Validity.NonNullable;
        }

        EnsureValidity(rows);
        Span<byte> bits = new Span<byte>(_validity, _validityBytes);
        if (_known < rows)
        {
            BitmapKernels.SetRange(bits, _known, rows - _known);
            _known = rows;
        }

        if (_known == rows && (rows & 7) != 0)
        {
            bits[rows >> 3] &= (byte)((1 << (rows & 7)) - 1);
        }

        VortexBuffer buffer = _validityOwner!.Buffer.Slice(0, (rows + 7) >> 3);
        return Validity.Bitmap(arena.AddBool(BitmapType, rows, Validity.NonNullable, buffer, 0));
    }

    protected void DiscardValidity(int rows)
    {
        if (!_nulls)
        {
            return;
        }

        if (_known <= rows)
        {
            _known = 0;
            _nulls = false;
            return;
        }

        Span<byte> bits = new Span<byte>(_validity, _validityBytes);
        BitmapKernels.CopyRange(bits, rows, bits, 0, _known - rows);
        _known -= rows;
    }

    protected void TruncateValidity(int rows)
    {
        if (_known > rows)
        {
            _known = rows;
        }

        if (_known == 0)
        {
            _nulls = false;
        }
    }

    protected long ValidityBytes(int rows) => _nulls ? (rows + 7L) >> 3 : 0;

    protected void ReleaseValidity()
    {
        if (_validityOwner is not null)
        {
            Pool.Return(_validityOwner);
            _validityOwner = null;
        }

        _validity = null;
        _validityBytes = 0;
        _known = 0;
        _nulls = false;
    }

    private void EnsureValidity(int rows)
    {
        int bytes = (rows + 7) >> 3;
        if (bytes <= _validityBytes)
        {
            return;
        }

        Grow(Pool, ref _validityOwner, ref _validity, ref _validityBytes, (_known + 7) >> 3, bytes);
    }

    /// <summary>
    /// Replaces a buffer with one of at least <paramref name="needed"/> bytes, keeping the first
    /// <paramref name="used"/>, and gives the old one back to the pool.
    /// </summary>
    protected static void Grow(
        AlignedBufferPool pool, ref NativeSegmentOwner? owner, ref byte* data, ref int capacity, long used, long needed)
    {
        const long Ceiling = int.MaxValue - 4096;
        if (needed > Ceiling)
        {
            throw new InvalidOperationException($"A builder column holds at most {Ceiling} bytes in one buffer; write the rows before they reach it.");
        }

        long doubled = Math.Max(4096L, (long)capacity * 2);
        long size = Math.Max(needed, doubled);
        size = size <= 1L << 30 ? (long)BitOperations.RoundUpToPowerOf2((ulong)size) : Math.Min(size, Ceiling);
        NativeSegmentOwner next = pool.Rent((int)size, VortexLimits.MaxAlignment);
        byte* target = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(next.WritableSpan));
        if (used > 0)
        {
            Buffer.MemoryCopy(data, target, size, used);
        }

        if (owner is not null)
        {
            pool.Return(owner);
        }

        owner = next;
        data = target;
        capacity = (int)size;
    }

    /// <summary>A view of the first <paramref name="length"/> bytes of a buffer, empty when there is none.</summary>
    protected static VortexBuffer View(NativeSegmentOwner? owner, int length) =>
        owner is null || length == 0 ? VortexBuffer.Empty : owner.Buffer.Slice(0, length);
}
