// Hand-written FlatBuffers builder. Zero dependency is a founding constraint
// (docs/03-architecture.md §1), so the writer half of the runtime is this file plus VTableCache.
//
// FlatBuffers is built BACK TO FRONT. Every object is emitted before the object that references
// it, positions are counted from the END of the scratch array, and the root uoffset is the last
// thing written. docs/01-scope.md §3 names back-to-front construction, alignment and vtable
// deduplication as "the hidden half of Phase 0" precisely because each is easy to half-implement.
//
// Wire shapes, transcribed from spec/flatbuffers/*.fbs and docs/02-format.md §2, §3:
//   buffer := [u32 root uoffset] ... objects ...
//   table  := [i32 soffset to vtable][inline field data]
//   vtable := [u16 vtable_size][u16 table_size][u16 slot per field id]
//   vector := [u32 count][elements]
//   string := [u32 length][utf8 bytes][NUL]        the NUL is NOT counted by the length prefix
//
// Two coordinate systems meet here and confusing them is the classic builder bug:
//   * a BACK-OFFSET is `capacity - space`, i.e. the number of bytes written so far. It is what
//     every public method returns and accepts, and it survives reallocation of the scratch array.
//   * an ABSOLUTE INDEX is a position in the scratch array, `capacity - backOffset`. It is never
//     handed out, because growing the buffer moves it.
// A uoffset stored at back-offset `p` (counted after the 4 bytes of the uoffset itself) and
// referring to back-offset `t` holds `p - t`; that is exactly `targetAbsolute - fieldAbsolute`
// once the buffer is flipped, so it is a forward reference as the reader requires.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Vorticity.Serialization.FlatBuffers;

/// <summary>
/// Builds a FlatBuffer back to front into a pooled scratch array, deduplicating vtables.
/// </summary>
/// <remarks>
/// <para>
/// Usage mirrors the format's own ordering rule: strings, vectors and child tables are created
/// <em>before</em> the table that references them, and a table is opened with
/// <see cref="StartTable"/>, filled with <c>Add…</c> calls and closed with <see cref="EndTable"/>.
/// Creating a string or a vector while a table is open is rejected — FlatBuffers has no way to
/// encode it, and silently accepting it produces a buffer no reader can follow.
/// </para>
/// <para>
/// <b>Vtable deduplication is mandatory, not an optimization</b> (docs/01-scope.md §3). Every
/// finished table's vtable is compared byte for byte against every vtable already written, and an
/// identical one is reused. Two tables with the same field ids but different value <em>widths</em>
/// are not identical: a vtable encodes <c>table_size</c> and per-field byte offsets, not just
/// which fields are present.
/// </para>
/// <para>
/// <b>Defaults.</b> A scalar equal to its schema default is omitted, so the reader returns the
/// default from the absent slot. Set <see cref="ForceDefaults"/> to emit it anyway. Fields
/// declared <c>= null</c> in a schema — <c>ArrayStats.is_sorted</c>, <c>null_count</c>,
/// <c>nan_count</c> in spec/flatbuffers/array.fbs — must distinguish "absent" from "present and
/// zero", so they are written with the <c>…Always</c> variants.
/// </para>
/// <para>
/// <b>Ownership.</b> The scratch array is rented from <see cref="ArrayPool{T}"/> and returned by
/// <see cref="Dispose"/>. The span returned by <see cref="Finish"/> points into it and is valid
/// only until <see cref="Clear"/> or <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class FlatBufferBuilder : IDisposable
{
    private const int DefaultCapacity = 1024;
    private const int MinimumCapacity = 64;

    /// <summary>
    /// FlatBuffers addresses everything with 32-bit offsets, so a buffer can never reach 2 GiB.
    /// The margin keeps the head padding and the root uoffset inside <see cref="int"/> arithmetic.
    /// </summary>
    private const int MaxBufferSize = int.MaxValue - 64;

    /// <summary>
    /// <c>vtable_size</c> is a <c>u16</c> covering a 4-byte header plus one <c>u16</c> per field,
    /// so the highest encodable field id is <c>(65535 / 2) - 2 - 1</c>.
    /// </summary>
    private const int MaxFieldId = (ushort.MaxValue / 2) - 3;

    /// <summary>
    /// The finished buffer is padded so its length is a multiple of 8. Placed at an 8-byte aligned
    /// address it then puts every object on its own natural boundary, which is what lets the
    /// reader reinterpret struct vectors in place (docs/03-architecture.md §3.5).
    /// </summary>
    private const int RootAlignment = 8;

    private byte[] _buffer;

    /// <summary>Unused bytes at the head. The written region is <c>[_space, _buffer.Length)</c>.</summary>
    private int _space;

    /// <summary>Largest alignment any object in this buffer demanded. Always a power of two.</summary>
    private int _minAlign;

    /// <summary>Back-offset of each field of the open table, indexed by field id. 0 means absent.</summary>
    private int[] _vtable;

    /// <summary>Highest used field id plus one, or <c>-1</c> when no table is open.</summary>
    private int _vtableSize;

    /// <summary><c>Offset</c> at <see cref="StartTable"/>, used to compute <c>table_size</c>.</summary>
    private int _objectStart;

    private readonly VTableCache _vtables;
    private bool _finished;
    private bool _disposed;

    /// <summary>Creates a builder over a pooled scratch array.</summary>
    /// <param name="initialCapacity">Capacity hint in bytes; the array grows on demand.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="initialCapacity"/> is negative or beyond the 2 GiB FlatBuffers ceiling.
    /// </exception>
    public FlatBufferBuilder(int initialCapacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialCapacity, MaxBufferSize);

        _buffer = ArrayPool<byte>.Shared.Rent(
            initialCapacity < MinimumCapacity ? MinimumCapacity : initialCapacity);
        _space = _buffer.Length;
        _minAlign = 1;
        _vtable = new int[16];
        _vtableSize = -1;
        _vtables = new VTableCache();
    }

    /// <summary>
    /// Bytes written so far, measured from the head of the back-to-front buffer. Every offset this
    /// builder returns is a value of this counter, so it is also the identity of the object most
    /// recently written.
    /// </summary>
    public int Offset => _buffer.Length - _space;

    /// <summary>
    /// When true, a scalar equal to its default is emitted rather than omitted. Default false.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch for a producer that must round-trip "present and equal to the
    /// default" through a reader that only sees presence. It does not affect
    /// <see cref="AddOffset"/>, whose <c>0</c> means "no such object", nor the <c>…Always</c>
    /// variants, which are unconditional by definition.
    /// </remarks>
    public bool ForceDefaults { get; set; }

    /// <summary>
    /// Number of distinct vtables in the buffer. Exposed for the dedup tests: a wide schema of
    /// uniform tables must converge on a handful of vtables, not one per table.
    /// </summary>
    internal int VTableCount => _vtables.Count;

    /// <summary>
    /// Discards everything written and makes the builder reusable, keeping the pooled array.
    /// Invalidates the span returned by <see cref="Finish"/>.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _space = _buffer.Length;
        _minAlign = 1;
        _vtableSize = -1;
        _objectStart = 0;
        _finished = false;
        _vtables.Clear();
    }

    /// <summary>Returns the pooled scratch array. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        byte[] buffer = _buffer;
        _buffer = [];
        _space = 0;
        _vtableSize = -1;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    // ---------------------------------------------------------------------------------------
    // Objects created before the table that references them.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a UTF-8 string and returns its offset. The trailing NUL FlatBuffers requires is
    /// appended and is <em>not</em> counted by the <c>u32</c> length prefix.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table is open, or the buffer is finished.</exception>
    public int CreateStringUtf8(ReadOnlySpan<byte> utf8)
    {
        RequireNoOpenTable();
        PutTerminator();
        StartVector(sizeof(byte), utf8.Length, 1);
        PutRaw(utf8);
        return EndVector(utf8.Length);
    }

    /// <summary>Encodes <paramref name="value"/> as UTF-8 and writes it as a FlatBuffers string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A table is open, or the buffer is finished.</exception>
    public int CreateString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireNoOpenTable();

        // Transcoded straight into the scratch array: the count is known up front, so no
        // intermediate byte[] is needed. Unpaired surrogates become U+FFFD, matching
        // Encoding.UTF8's replacement fallback, which is what ProtoWriter.WriteString does too.
        int byteCount = Encoding.UTF8.GetByteCount(value);
        PutTerminator();
        StartVector(sizeof(byte), byteCount, 1);
        if (byteCount > _space)
        {
            Grow(byteCount);
        }

        _space -= byteCount;
        int written = Encoding.UTF8.GetBytes(value.AsSpan(), _buffer.AsSpan(_space, byteCount));
        Debug.Assert(written == byteCount, "GetByteCount and GetBytes must agree.");
        return EndVector(byteCount);
    }

    /// <summary>Writes a <c>[ubyte]</c> vector and returns its offset.</summary>
    /// <exception cref="InvalidOperationException">A table is open, or the buffer is finished.</exception>
    public int CreateByteVector(ReadOnlySpan<byte> bytes)
    {
        RequireNoOpenTable();
        StartVector(sizeof(byte), bytes.Length, 1);
        PutRaw(bytes);
        return EndVector(bytes.Length);
    }

    /// <summary>
    /// Writes a vector of scalars, aligned to <typeparamref name="T"/>'s natural alignment.
    /// </summary>
    /// <remarks>
    /// For every FlatBuffers scalar type <c>alignof(T) == sizeof(T)</c>, so this is the alignment
    /// the format specifies. The elements are copied as one block, so element 0 lands at the
    /// lowest address exactly as the wire format requires.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A table is open, or the buffer is finished.</exception>
    /// <exception cref="VortexFormatException">The vector would exceed the 2 GiB buffer ceiling.</exception>
    public int CreateScalarVector<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        CreateVectorCore(values, FlatBufferAccess.AlignmentOf<T>());

    /// <summary>
    /// Writes a vector of inline FlatBuffers structs, aligned to <c>alignof(T)</c>.
    /// </summary>
    /// <remarks>
    /// The alignment is <c>alignof(T)</c> and never <c>sizeof(T)</c>: <c>struct Buffer</c> in
    /// spec/flatbuffers/array.fbs is 8 bytes but only 4-byte aligned, and over-aligning it would
    /// produce a file that disagrees with every other writer. This is the same rule
    /// <see cref="FlatBufferTable.GetStructVector{T}"/> checks on read, so anything written here
    /// reinterprets in place there.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A table is open, or the buffer is finished.</exception>
    /// <exception cref="VortexFormatException">The vector would exceed the 2 GiB buffer ceiling.</exception>
    public int CreateStructVector<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        CreateVectorCore(values, FlatBufferAccess.AlignmentOf<T>());

    /// <summary>
    /// Writes a vector of references to strings, vectors or tables created earlier. Elements
    /// appear in the order given.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table is open, or the buffer is finished.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An element is <c>0</c>, negative, or names an object that has not been written yet. A
    /// zero element is rejected rather than written, because a zero uoffset is what the reader
    /// treats as malformed.
    /// </exception>
    public int CreateOffsetVector(ReadOnlySpan<int> offsets)
    {
        RequireNoOpenTable();
        StartVector(sizeof(uint), offsets.Length, sizeof(uint));

        // Written last to first so that, in the finished buffer, element 0 sits at the lowest
        // address. The back-to-front builder reverses everything exactly once.
        for (int i = offsets.Length - 1; i >= 0; i--)
        {
            int target = offsets[i];
            RequireWrittenOffset(target);
            PutUOffsetTo(target);
        }

        return EndVector(offsets.Length);
    }

    // ---------------------------------------------------------------------------------------
    // Tables.
    // ---------------------------------------------------------------------------------------

    /// <summary>Opens a table. Fields may be added in any order; the vtable records where each landed.</summary>
    /// <exception cref="InvalidOperationException">A table is already open, or the buffer is finished.</exception>
    public void StartTable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            ThrowFinished();
        }

        if (_vtableSize >= 0)
        {
            ThrowNestedTable();
        }

        _vtableSize = 0;
        _objectStart = Offset;
    }

    /// <summary>Adds a <c>byte</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddInt8(int fieldId, sbyte value, sbyte defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt8(fieldId, (byte)value);
    }

    /// <summary>Adds a <c>ubyte</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddUInt8(int fieldId, byte value, byte defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt8(fieldId, value);
    }

    /// <summary>Adds a <c>short</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddInt16(int fieldId, short value, short defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt16(fieldId, (ushort)value);
    }

    /// <summary>Adds a <c>ushort</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddUInt16(int fieldId, ushort value, ushort defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt16(fieldId, value);
    }

    /// <summary>Adds an <c>int</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddInt32(int fieldId, int value, int defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt32(fieldId, (uint)value);
    }

    /// <summary>Adds a <c>uint</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddUInt32(int fieldId, uint value, uint defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt32(fieldId, value);
    }

    /// <summary>Adds a <c>long</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddInt64(int fieldId, long value, long defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt64(fieldId, (ulong)value);
    }

    /// <summary>Adds a <c>ulong</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddUInt64(int fieldId, ulong value, ulong defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt64(fieldId, value);
    }

    /// <summary>Adds a <c>float</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    /// <remarks>
    /// The comparison is IEEE <c>==</c>, matching what flatc-generated builders emit. Two
    /// consequences are deliberate: <c>-0.0</c> against a <c>0.0</c> default is omitted and reads
    /// back as <c>+0.0</c>, and a NaN is never equal to anything so it is always emitted. Use
    /// <see cref="ForceDefaults"/> when a negative zero has to survive.
    /// </remarks>
    public void AddFloat32(int fieldId, float value, float defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt32(fieldId, BitConverter.SingleToUInt32Bits(value));
    }

    /// <summary>Adds a <c>double</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    /// <remarks>See <see cref="AddFloat32"/> for the negative-zero and NaN behaviour.</remarks>
    public void AddFloat64(int fieldId, double value, double defaultValue = 0)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt64(fieldId, BitConverter.DoubleToUInt64Bits(value));
    }

    /// <summary>Adds a <c>bool</c> field, omitting it when it equals <paramref name="defaultValue"/>.</summary>
    public void AddBool(int fieldId, bool value, bool defaultValue = false)
    {
        RequireOpenTable();
        if (value == defaultValue && !ForceDefaults)
        {
            return;
        }

        PutFieldUInt8(fieldId, value ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Adds a <c>uint64</c> field unconditionally, so that <c>0</c> is distinguishable from absent.
    /// </summary>
    /// <remarks>
    /// For FlatBuffers <c>= null</c> fields: <c>ArrayStats.null_count</c>,
    /// <c>uncompressed_size_in_bytes</c> and <c>nan_count</c> in spec/flatbuffers/array.fbs. A
    /// null_count of 0 means "no nulls"; an absent null_count means "unknown", and the reader's
    /// <see cref="FlatBufferTable.TryGetUInt64"/> is what tells them apart.
    /// </remarks>
    public void AddUInt64Always(int fieldId, ulong value)
    {
        RequireOpenTable();
        PutFieldUInt64(fieldId, value);
    }

    /// <summary>
    /// Adds a <c>bool</c> field unconditionally, so that <see langword="false"/> is
    /// distinguishable from absent.
    /// </summary>
    /// <remarks>
    /// For FlatBuffers <c>= null</c> fields: <c>ArrayStats.is_sorted</c>, <c>is_strict_sorted</c>
    /// and <c>is_constant</c> in spec/flatbuffers/array.fbs, where "not sorted" and "nobody
    /// computed it" are different claims (docs/08-semantics.md §5, class II).
    /// </remarks>
    public void AddBoolAlways(int fieldId, bool value)
    {
        RequireOpenTable();
        PutFieldUInt8(fieldId, value ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Adds a <c>ubyte</c> field unconditionally, the tri-state counterpart of
    /// <see cref="FlatBufferTable.TryGetUInt8"/>.
    /// </summary>
    /// <remarks>
    /// Not required by any <c>= null</c> field in the vendored schemas today, but
    /// <c>ArrayStats.min_precision</c> is a <c>uint8</c> enum whose zero value (<c>Inexact</c>) a
    /// producer may need to assert rather than imply.
    /// </remarks>
    public void AddUInt8Always(int fieldId, byte value)
    {
        RequireOpenTable();
        PutFieldUInt8(fieldId, value);
    }

    /// <summary>
    /// Adds a reference to a string, vector or table created earlier. An
    /// <paramref name="offset"/> of <c>0</c> writes nothing, which is how an absent optional
    /// child is expressed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative or names an object that has not been written yet.
    /// </exception>
    public void AddOffset(int fieldId, int offset)
    {
        RequireOpenTable();
        if (offset == 0)
        {
            return;
        }

        RequireWrittenOffset(offset);
        Prep(sizeof(uint), 0);
        PutUOffsetTo(offset);
        Slot(fieldId);
    }

    /// <summary>
    /// Adds an inline FlatBuffers <c>struct</c>, stored in the table body rather than referenced.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="T"/>'s .NET sequential layout is written verbatim, which is the same
    /// assumption <see cref="FlatBufferTable.TryGetStruct{T}"/> makes when reading it back. That
    /// holds for the schemas in spec/flatbuffers — <c>SegmentSpec</c> (16 B, 8-aligned) and
    /// <c>Buffer</c> (8 B, 4-aligned) both have no interior padding a FlatBuffers struct would
    /// not also have — and a type that needs different packing must say so with
    /// <see cref="StructLayoutAttribute"/>.
    /// </remarks>
    public void AddStruct<T>(int fieldId, in T value) where T : unmanaged
    {
        RequireOpenTable();
        int size = Unsafe.SizeOf<T>();
        Prep(FlatBufferAccess.AlignmentOf<T>(), size);
        _space -= size;
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(_buffer.AsSpan(_space, size)), value);
        Slot(fieldId);
    }

    /// <summary>
    /// Closes the table, writes its vtable, deduplicates that vtable against every one already
    /// written, and returns the table's offset.
    /// </summary>
    /// <exception cref="InvalidOperationException">No table is open.</exception>
    /// <exception cref="VortexFormatException">
    /// The table body exceeds the 65535 bytes a <c>u16 table_size</c> can express.
    /// </exception>
    public int EndTable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vtableSize < 0)
        {
            ThrowNoOpenTable();
        }

        // The table starts with a soffset to its vtable. Reserve it now and backpatch below, once
        // dedup has decided which vtable this table actually points at.
        Prep(sizeof(int), 0);
        PutUInt32(0);
        int tableOffset = Offset;

        int tableSize = tableOffset - _objectStart;
        if (tableSize > ushort.MaxValue)
        {
            ThrowTableTooLarge(tableSize);
        }

        // Trailing absent fields are not encoded at all: the reader treats a field id past the end
        // of the vtable exactly like a zero slot (docs/03-architecture.md §6), so a sparse wide
        // table costs slots only up to its last present field. Slot() already stops _vtableSize at
        // the highest id actually written, so this loop normally exits at once; it enforces the
        // invariant rather than assuming it, and it is what a StartTable(fieldCount) overload
        // would need.
        int last = _vtableSize - 1;
        while (last >= 0 && _vtable[last] == 0)
        {
            last--;
        }

        int slotCount = last + 1;
        for (int i = last; i >= 0; i--)
        {
            int fieldOffset = _vtable[i];
            Prep(sizeof(ushort), 0);
            PutUInt16(fieldOffset == 0 ? (ushort)0 : (ushort)(tableOffset - fieldOffset));
        }

        Prep(sizeof(ushort), 0);
        PutUInt16((ushort)tableSize);

        int vtableBytes = (slotCount + 2) * sizeof(ushort);
        Prep(sizeof(ushort), 0);
        PutUInt16((ushort)vtableBytes);

        // Every Prep above may have reallocated, so the absolute view is taken only now.
        int candidateOffset = Offset;
        int capacity = _buffer.Length;
        ReadOnlySpan<byte> candidate =
            new ReadOnlySpan<byte>(_buffer, capacity - candidateOffset, vtableBytes);
        uint hash = VTableCache.Hash(candidate);
        int existing = _vtables.Find(_buffer, capacity, candidate, hash);

        int vtableOffset;
        if (existing != 0)
        {
            // Rewind past the vtable just written and point the table at the identical one. The
            // discarded bytes sit strictly below the new head, so no cached vtable is clobbered.
            _space = capacity - tableOffset;
            vtableOffset = existing;
        }
        else
        {
            _vtables.Add(candidateOffset, vtableBytes, hash);
            vtableOffset = candidateOffset;
        }

        // soffset = vtableBackOffset - tableBackOffset, which is tableAbsolute - vtableAbsolute
        // once the buffer is flipped. A fresh vtable was written after the table, so the value is
        // positive and the vtable precedes the table; a reused one was written before it, so the
        // value is negative and the vtable follows. Both are legal and both occur here, which is
        // exactly why the reader bounds soffsets in two directions.
        BinaryPrimitives.WriteInt32LittleEndian(
            _buffer.AsSpan(capacity - tableOffset, sizeof(int)), vtableOffset - tableOffset);

        _vtableSize = -1;
        return tableOffset;
    }

    // ---------------------------------------------------------------------------------------
    // Finishing.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the root uoffset and returns the finished buffer. The span is valid until
    /// <see cref="Clear"/> or <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// The head is padded so the result's length is a multiple of 8 and, placed at an 8-byte
    /// aligned address, every object inside lands on its own natural boundary. Since a position
    /// inside the buffer is <c>length - backOffset</c> and both terms are multiples of the
    /// object's alignment, that padding is the whole guarantee.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A table is still open, or the buffer has already been finished — call <see cref="Clear"/>
    /// to build another one.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rootTableOffset"/> is not an offset this builder has returned.
    /// </exception>
    public ReadOnlySpan<byte> Finish(int rootTableOffset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            ThrowFinished();
        }

        if (_vtableSize >= 0)
        {
            ThrowOpenTableAtFinish();
        }

        RequireWrittenOffset(rootTableOffset);

        int alignment = _minAlign < RootAlignment ? RootAlignment : _minAlign;
        Prep(alignment, sizeof(uint));
        PutUOffsetTo(rootTableOffset);
        _finished = true;
        return new ReadOnlySpan<byte>(_buffer, _space, _buffer.Length - _space);
    }

    /// <summary>Finishes the buffer and copies it into a fresh array.</summary>
    public byte[] FinishToArray(int rootTableOffset) => Finish(rootTableOffset).ToArray();

    /// <summary>
    /// Finishes the buffer and lends it as memory, valid until the builder is cleared or disposed.
    /// </summary>
    /// <remarks>
    /// The form an asynchronous sink takes without a copy: a span cannot cross an <c>await</c>,
    /// and <see cref="FinishToArray"/> allocates the buffer's length again.
    /// </remarks>
    public ReadOnlyMemory<byte> FinishMemory(int rootTableOffset)
    {
        int length = Finish(rootTableOffset).Length;
        return new ReadOnlyMemory<byte>(_buffer, _buffer.Length - length, length);
    }

    // ---------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Pads so that, after <paramref name="additionalBytes"/> more bytes are written, the next
    /// object starts on an <paramref name="alignment"/> boundary; then guarantees the room for
    /// both.
    /// </summary>
    /// <remarks>
    /// Offsets grow downwards, so "aligned" means the back-offset after the write is a multiple of
    /// <paramref name="alignment"/>; <see cref="Finish"/> pads the head to <see cref="_minAlign"/>
    /// so that translates into an aligned absolute position.
    /// </remarks>
    private void Prep(int alignment, long additionalBytes)
    {
        Debug.Assert(alignment > 0 && (alignment & (alignment - 1)) == 0, "alignment must be a power of two");

        if (alignment > _minAlign)
        {
            _minAlign = alignment;
        }

        // Computed in 64 bits: additionalBytes is an element count times an element size and a
        // hostile caller must get a rejection, not a wrapped padding value.
        long padding = -((long)Offset + additionalBytes) & (alignment - 1);
        long required = padding + alignment + additionalBytes;
        if (required > _space)
        {
            Grow(required);
        }

        if (padding > 0)
        {
            _space -= (int)padding;
            _buffer.AsSpan(_space, (int)padding).Clear();
        }
    }

    private void Grow(long required)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int capacity = _buffer.Length;
        int written = capacity - _space;
        long needed = written + required;
        if (needed > MaxBufferSize)
        {
            ThrowBufferTooLarge(needed);
        }

        long next = capacity * 2L;
        if (next < needed)
        {
            next = needed;
        }

        if (next > MaxBufferSize)
        {
            next = MaxBufferSize;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent((int)next);

        // Positions are measured from the END, so the written region is copied to the end of the
        // new array and every offset this builder has already handed out stays valid.
        Array.Copy(_buffer, _space, grown, grown.Length - written, written);
        byte[] old = _buffer;
        _buffer = grown;
        _space = grown.Length - written;
        ArrayPool<byte>.Shared.Return(old);
    }

    /// <summary>
    /// Reserves and aligns room for a vector: the <c>u32</c> count must land 4-aligned and the
    /// first element on <paramref name="alignment"/>, with the count immediately below the
    /// elements.
    /// </summary>
    private void StartVector(int elementSize, int count, int alignment)
    {
        long bytes = (long)elementSize * count;
        if (bytes > MaxBufferSize)
        {
            ThrowVectorTooLarge(count, elementSize);
        }

        Prep(sizeof(uint), bytes);
        Prep(alignment, bytes);
    }

    private int EndVector(int count)
    {
        // StartVector aligned the head so that, after exactly `bytes` element bytes, the count
        // prefix lands 4-aligned with no padding between it and the first element. Padding here
        // would silently detach the two.
        Debug.Assert((Offset & 3) == 0, "the element bytes must leave the count prefix 4-aligned");
        if (_space < sizeof(uint))
        {
            Grow(sizeof(uint));
        }

        PutUInt32((uint)count);
        return Offset;
    }

    private int CreateVectorCore<T>(ReadOnlySpan<T> values, int alignment) where T : unmanaged
    {
        RequireNoOpenTable();
        StartVector(Unsafe.SizeOf<T>(), values.Length, alignment);
        PutRaw(MemoryMarshal.AsBytes(values));
        return EndVector(values.Length);
    }

    /// <summary>
    /// Writes the NUL that terminates a FlatBuffers string. It goes in first because the builder
    /// runs backwards, and it is deliberately outside the length prefix the vector then writes.
    /// </summary>
    private void PutTerminator()
    {
        Prep(sizeof(byte), 0);
        PutUInt8(0);
    }

    private void PutFieldUInt8(int fieldId, byte value)
    {
        Prep(sizeof(byte), 0);
        PutUInt8(value);
        Slot(fieldId);
    }

    private void PutFieldUInt16(int fieldId, ushort value)
    {
        Prep(sizeof(ushort), 0);
        PutUInt16(value);
        Slot(fieldId);
    }

    private void PutFieldUInt32(int fieldId, uint value)
    {
        Prep(sizeof(uint), 0);
        PutUInt32(value);
        Slot(fieldId);
    }

    private void PutFieldUInt64(int fieldId, ulong value)
    {
        Prep(sizeof(ulong), 0);
        PutUInt64(value);
        Slot(fieldId);
    }

    /// <summary>Records where the value of <paramref name="fieldId"/> was written.</summary>
    private void Slot(int fieldId)
    {
        if ((uint)fieldId > MaxFieldId)
        {
            ThrowFieldId(fieldId);
        }

        if (fieldId >= _vtable.Length)
        {
            Array.Resize(ref _vtable, Math.Max(fieldId + 1, _vtable.Length * 2));
        }

        if (fieldId >= _vtableSize)
        {
            // The slots between the highest id used so far and this one may hold values left by a
            // previous table, and they must read as absent.
            Array.Clear(_vtable, _vtableSize, fieldId - _vtableSize);
            _vtableSize = fieldId + 1;
        }
        else if (_vtable[fieldId] != 0)
        {
            ThrowDuplicateField(fieldId);
        }

        // Offset is at least 1 here - the value was just written - so 0 stays a usable "absent"
        // sentinel.
        _vtable[fieldId] = Offset;
    }

    /// <summary>Writes a forward reference to the object at back-offset <paramref name="target"/>.</summary>
    private void PutUOffsetTo(int target)
    {
        if (_space < sizeof(uint))
        {
            Grow(sizeof(uint));
        }

        int current = Offset;
        PutUInt32((uint)(current + sizeof(uint) - target));
    }

    private void PutRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _space)
        {
            Grow(bytes.Length);
        }

        _space -= bytes.Length;
        bytes.CopyTo(_buffer.AsSpan(_space, bytes.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutUInt8(byte value) => _buffer[--_space] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutUInt16(ushort value)
    {
        _space -= sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_space, sizeof(ushort)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutUInt32(uint value)
    {
        _space -= sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_space, sizeof(uint)), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutUInt64(ulong value)
    {
        _space -= sizeof(ulong);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_space, sizeof(ulong)), value);
    }

    private void RequireOpenTable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vtableSize < 0)
        {
            ThrowNoOpenTable();
        }
    }

    private void RequireNoOpenTable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            ThrowFinished();
        }

        if (_vtableSize >= 0)
        {
            ThrowNestedTable();
        }
    }

    private void RequireWrittenOffset(int offset)
    {
        // An offset names an object already in the buffer. 0 is never one: it is the empty buffer,
        // and a zero uoffset is what the reader rejects as malformed.
        if (offset <= 0 || offset > Offset)
        {
            ThrowUnwrittenOffset(offset, Offset);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoOpenTable() =>
        throw new InvalidOperationException(
            "FlatBufferBuilder: no table is open. Call StartTable() before adding fields.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNestedTable() =>
        throw new InvalidOperationException(
            "FlatBufferBuilder: a table is already open. FlatBuffers cannot nest a table, string " +
            "or vector inside a table under construction; create it before StartTable().");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOpenTableAtFinish() =>
        throw new InvalidOperationException(
            "FlatBufferBuilder: a table is still open. Call EndTable() before Finish().");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFinished() =>
        throw new InvalidOperationException(
            "FlatBufferBuilder: the buffer is already finished. Call Clear() to build another one.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDuplicateField(int fieldId) =>
        throw new InvalidOperationException(
            $"FlatBufferBuilder: field {fieldId} has already been added to the open table.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFieldId(int fieldId) =>
        throw new ArgumentOutOfRangeException(
            nameof(fieldId), fieldId,
            $"FlatBuffers field ids run from 0 to {MaxFieldId}: vtable_size is a u16 covering a " +
            "4-byte header plus one u16 per field.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnwrittenOffset(int offset, int written) =>
        throw new ArgumentOutOfRangeException(
            nameof(offset), offset,
            $"FlatBufferBuilder: {offset} is not an offset this builder has returned; only " +
            $"1..{written} have been written. FlatBuffers is built back to front, so a referenced " +
            "object must be created before the object that references it.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBufferTooLarge(long needed) =>
        throw new VortexFormatException(
            $"FlatBuffers buffer would need {needed} bytes; the format addresses objects with " +
            $"32-bit offsets and cannot exceed {MaxBufferSize}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTableTooLarge(int tableSize) =>
        throw new VortexFormatException(
            $"FlatBuffers table body is {tableSize} bytes; table_size is a u16 and cannot exceed " +
            $"{ushort.MaxValue}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowVectorTooLarge(int count, int elementSize) =>
        throw new VortexFormatException(
            $"FlatBuffers vector of {count} elements of {elementSize} bytes exceeds the " +
            $"{MaxBufferSize}-byte buffer ceiling.");
}
