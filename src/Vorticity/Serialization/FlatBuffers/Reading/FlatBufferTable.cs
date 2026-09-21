using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization.FlatBuffers;

/// <summary>
/// A read-only accessor over one FlatBuffers table inside an untrusted buffer.
/// </summary>
/// <remarks>
/// <para>
/// A table is <c>[soffset_t vtable_ref][inline field data]</c>; its vtable is
/// <c>[u16 vtable_size][u16 table_size][u16 field slots...]</c>. A field id beyond the vtable's own
/// length, or whose slot is <c>0</c>, means the field is <em>absent</em> and the accessor returns
/// the schema default. A slot that points past <c>table_size</c> is malformed.
/// </para>
/// <para>
/// Every accessor throws <see cref="VortexFormatException"/> - and only that - on malformed input.
/// A <c>fieldId</c> that is negative or beyond the vtable is treated as absent, exactly
/// like a zero slot: field ids come from our own transcribed schemas, never from the file.
/// </para>
/// <para>
/// There is no separate verification pass: every uoffset, soffset and vtable read is bounds-checked
/// at the moment it is dereferenced, so a malformed buffer cannot produce an out-of-range read.
/// Uoffsets point forward and are unsigned, so progression is monotonic and cycles are impossible
/// by construction - there is deliberately no cycle detection and no visited set here. Forward-only
/// offsets do not exclude sharing, however: two slots at different positions may legally resolve to
/// the same target, so the object graph is a directed acyclic graph rather than a tree, and a
/// consumer that walks it as a tree pays exponentially in its depth. Depth alone therefore cannot
/// bound a traversal, which is why the optional caller-owned budget bounds the total number of
/// tables visited instead. A table's vtable reference is signed, the vtable may sit on either side
/// of its table, and vtable sharing between tables is legal and routine, so a revisited position is
/// never rejected.
/// </para>
/// </remarks>
internal readonly ref struct FlatBufferTable
{
    private readonly ReadOnlySpan<byte> _buffer;

    // Caller-owned remaining table budget, shared by reference with every table and vector reached
    // from this one. A null ref means "no budget": the traversal's owner bounds its own work.
    private readonly ref int _tableBudget;

    private readonly int _tablePos;
    private readonly int _vtablePos;
    private readonly int _vtableSize;
    private readonly int _tableSize;
    private readonly int _depth;

    /// <summary>
    /// Resolves and validates the vtable of the table at <paramref name="tablePos"/>, charging one
    /// table to <paramref name="tableBudget"/> when the traversal carries one.
    /// </summary>
    internal FlatBufferTable(ReadOnlySpan<byte> buffer, int tablePos, int depth, ref int tableBudget)
    {
        // Charged before anything is read, so a shared-child DAG is stopped by total work and not
        // only by depth. `Unsafe.IsNullRef` is a pointer compare; a budget-less traversal (and the
        // null table, which is `default`) pays one predictable branch.
        if (!Unsafe.IsNullRef(ref tableBudget))
        {
            if (--tableBudget < 0)
            {
                ThrowTableBudget();
            }
        }

        // The vtable reference is signed and the vtable may sit either side of the table, so the
        // subtraction is done in 64 bits: soffset == int.MinValue would otherwise wrap a position
        // 2 GiB past the table back into range.
        int soffset = FlatBufferAccess.ReadInt32(buffer, tablePos);
        long vtablePos = (long)tablePos - soffset;
        if (vtablePos < 0 || vtablePos > buffer.Length - 4)
        {
            ThrowVTableOutOfBounds(tablePos, soffset, buffer.Length);
        }

        int vtableSize = FlatBufferAccess.ReadUInt16(buffer, (int)vtablePos);
        // A vtable always carries at least its own two u16 header fields. An odd vtable_size is
        // tolerated (the trailing half slot is simply not addressable) rather than rejected: it
        // costs nothing and refusing it would be an over-rejection no rule asks for.
        if (vtableSize < 4 || vtablePos > buffer.Length - vtableSize)
        {
            ThrowVTableSize(vtableSize, (int)vtablePos, buffer.Length);
        }

        int tableSize = FlatBufferAccess.ReadUInt16(buffer, (int)vtablePos + 2);
        // table_size covers the soffset itself, so it is at least 4, and the whole table must lie
        // inside the buffer: this is the single check that lets every later field access compare
        // against table_size alone.
        if (tableSize < 4 || tablePos > buffer.Length - tableSize)
        {
            ThrowTableSize(tableSize, tablePos, buffer.Length);
        }

        _buffer = buffer;
        _tableBudget = ref tableBudget;
        _tablePos = tablePos;
        _vtablePos = (int)vtablePos;
        _vtableSize = vtableSize;
        _tableSize = tableSize;
        _depth = depth;
    }

    /// <summary>
    /// Reads the root uoffset at position 0 of <paramref name="buffer"/> and returns the root table.
    /// </summary>
    /// <remarks>
    /// The traversal reached from this table carries no total-table budget: forward-only uoffsets
    /// exclude cycles but not sharing, so a recursive consumer must bound its own work — either by
    /// memoising revisited positions or by using
    /// <see cref="Root(ReadOnlySpan{byte}, ref int)"/> with
    /// <see cref="VortexLimits.MaxFlatBufferTables"/>.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The buffer is shorter than 4 bytes, the root offset is zero, or it points out of range.
    /// </exception>
    public static FlatBufferTable Root(ReadOnlySpan<byte> buffer)
    {
        int rootPos = RootPosition(buffer);
        return new FlatBufferTable(buffer, rootPos, 0, ref Unsafe.NullRef<int>());
    }

    /// <summary>
    /// Reads the root table and charges every table reached from it — however deeply, and however
    /// often a shared position is revisited — to <paramref name="tableBudget"/>.
    /// </summary>
    /// <param name="buffer">The whole FlatBuffer, starting at its root uoffset.</param>
    /// <param name="tableBudget">
    /// Remaining table allowance, owned by the caller and shared by reference with the whole
    /// traversal. Seed it with <see cref="VortexLimits.MaxFlatBufferTables"/>. It must outlive
    /// every table and vector reached from the result.
    /// </param>
    /// <exception cref="VortexFormatException">
    /// The buffer is shorter than 4 bytes, the root offset is zero or points out of range, or the
    /// budget is exhausted.
    /// </exception>
    public static FlatBufferTable Root(ReadOnlySpan<byte> buffer, ref int tableBudget)
    {
        int rootPos = RootPosition(buffer);
        return new FlatBufferTable(buffer, rootPos, 0, ref tableBudget);
    }

    private static int RootPosition(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4)
        {
            ThrowRootTruncated(buffer.Length);
        }

        uint rootOffset = FlatBufferAccess.ReadUInt32(buffer, 0);
        // uoffsets are unsigned and point forward; 0 is never a valid reference.
        // The target must have room for at least the table's own soffset.
        long rootPos = rootOffset;
        if (rootOffset == 0 || rootPos > buffer.Length - 4)
        {
            ThrowRootOffset(rootOffset, buffer.Length);
        }

        return (int)rootPos;
    }

    /// <summary>
    /// Absolute position of this table inside its buffer. It is the identity a consumer memoises
    /// on when it walks a shared (DAG-shaped) object graph.
    /// </summary>
    internal int Position => _tablePos;

    /// <summary>Length of the buffer this table lives in.</summary>
    internal int BufferLength => _buffer.Length;

    /// <summary>True for an absent sub-table (the result of <see cref="GetTable"/> on an absent field).</summary>
    public bool IsNull => _vtableSize == 0;

    /// <summary>
    /// True when the vtable carries a non-zero slot for <paramref name="fieldId"/> (the 0-based
    /// schema field index).
    /// </summary>
    /// <exception cref="VortexFormatException">The slot points outside the table body.</exception>
    public bool HasField(int fieldId) => TryGetFieldPos(fieldId, 1, out _);

    /// <summary>Reads a <c>byte</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public sbyte GetInt8(int fieldId, sbyte defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(sbyte), out int pos)
            ? (sbyte)FlatBufferAccess.ReadUInt8(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>ubyte</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public byte GetUInt8(int fieldId, byte defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(byte), out int pos)
            ? FlatBufferAccess.ReadUInt8(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>short</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public short GetInt16(int fieldId, short defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(short), out int pos)
            ? (short)FlatBufferAccess.ReadUInt16(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>ushort</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public ushort GetUInt16(int fieldId, ushort defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(ushort), out int pos)
            ? FlatBufferAccess.ReadUInt16(_buffer, pos)
            : defaultValue;

    /// <summary>Reads an <c>int</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public int GetInt32(int fieldId, int defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(int), out int pos)
            ? FlatBufferAccess.ReadInt32(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>uint</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public uint GetUInt32(int fieldId, uint defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(uint), out int pos)
            ? FlatBufferAccess.ReadUInt32(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>long</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public long GetInt64(int fieldId, long defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(long), out int pos)
            ? (long)FlatBufferAccess.ReadUInt64(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>ulong</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public ulong GetUInt64(int fieldId, ulong defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(ulong), out int pos)
            ? FlatBufferAccess.ReadUInt64(_buffer, pos)
            : defaultValue;

    /// <summary>Reads a <c>float</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public float GetFloat32(int fieldId, float defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(float), out int pos)
            ? BitConverter.UInt32BitsToSingle(FlatBufferAccess.ReadUInt32(_buffer, pos))
            : defaultValue;

    /// <summary>Reads a <c>double</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    public double GetFloat64(int fieldId, double defaultValue = 0) =>
        TryGetFieldPos(fieldId, sizeof(double), out int pos)
            ? BitConverter.UInt64BitsToDouble(FlatBufferAccess.ReadUInt64(_buffer, pos))
            : defaultValue;

    /// <summary>Reads a <c>bool</c> field, or <paramref name="defaultValue"/> when absent.</summary>
    /// <remarks>Any non-zero byte is <see langword="true"/>, as FlatBuffers specifies.</remarks>
    public bool GetBool(int fieldId, bool defaultValue = false) =>
        TryGetFieldPos(fieldId, sizeof(byte), out int pos)
            ? FlatBufferAccess.ReadUInt8(_buffer, pos) != 0
            : defaultValue;

    /// <summary>
    /// Tri-state read of a FlatBuffers <c>bool = null</c> field
    /// (<c>ArrayStats.is_sorted</c>, <c>is_strict_sorted</c>, <c>is_constant</c>).
    /// </summary>
    /// <returns><see langword="false"/> when the field is absent, which means "unknown", not "false".</returns>
    public bool TryGetBool(int fieldId, out bool value)
    {
        if (TryGetFieldPos(fieldId, sizeof(byte), out int pos))
        {
            value = FlatBufferAccess.ReadUInt8(_buffer, pos) != 0;
            return true;
        }

        value = false;
        return false;
    }

    /// <summary>
    /// Tri-state read of a FlatBuffers <c>uint64 = null</c> field
    /// (<c>ArrayStats.null_count</c>, <c>uncompressed_size_in_bytes</c>, <c>nan_count</c>).
    /// </summary>
    public bool TryGetUInt64(int fieldId, out ulong value)
    {
        if (TryGetFieldPos(fieldId, sizeof(ulong), out int pos))
        {
            value = FlatBufferAccess.ReadUInt64(_buffer, pos);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Tri-state read of a FlatBuffers <c>ubyte = null</c> field.</summary>
    public bool TryGetUInt8(int fieldId, out byte value)
    {
        if (TryGetFieldPos(fieldId, sizeof(byte), out int pos))
        {
            value = FlatBufferAccess.ReadUInt8(_buffer, pos);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Reads a sub-table field. An absent field yields a table whose <see cref="IsNull"/> is true.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The uoffset is zero or out of range, the sub-table's vtable is malformed, or the traversal
    /// exceeds <see cref="VortexLimits.MaxFlatBufferDepth"/>.
    /// </exception>
    public FlatBufferTable GetTable(int fieldId)
    {
        if (!TryGetFieldPos(fieldId, sizeof(uint), out int pos))
        {
            return default;
        }

        int target = ResolveOffset(_buffer, pos);
        int depth = _depth + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxFlatBufferDepth, "FlatBuffers table");
        return new FlatBufferTable(_buffer, target, depth, ref _tableBudget);
    }

    /// <summary>
    /// Reads a <c>string</c> field as its UTF-8 bytes, excluding the trailing NUL. Absent yields
    /// an empty span.
    /// </summary>
    public ReadOnlySpan<byte> GetStringUtf8(int fieldId)
    {
        if (!TryGetFieldPos(fieldId, sizeof(uint), out int pos))
        {
            return default;
        }

        return ReadString(_buffer, ResolveOffset(_buffer, pos));
    }

    /// <summary>Reads a <c>[ubyte]</c> field. Absent yields an empty span.</summary>
    public ReadOnlySpan<byte> GetByteVector(int fieldId)
    {
        if (!TryGetFieldPos(fieldId, sizeof(uint), out int pos))
        {
            return default;
        }

        int vectorPos = ResolveOffset(_buffer, pos);
        uint count = FlatBufferAccess.ReadUInt32(_buffer, vectorPos);
        FlatBufferAccess.CheckRange(_buffer, (long)vectorPos + 4, count, "byte vector");
        return _buffer.Slice(vectorPos + 4, (int)count);
    }

    /// <summary>
    /// Reinterprets a <c>[T]</c> field - a vector of scalars or of inline FlatBuffers structs - as
    /// a span of <typeparamref name="T"/> with no copy and no traversal. Absent yields an empty span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Footer.segment_specs</c> (<c>SegmentSpec</c>, 16 B) and <c>Array.buffers</c>
    /// (<c>Buffer</c>, 8 B) are read this way. The element count times
    /// <c>sizeof(T)</c> is computed in 64 bits so a hostile <c>uint32</c> count cannot overflow
    /// into a range that looks valid.
    /// </para>
    /// <para>
    /// The alignment tested is that of the element <em>address</em>, and the requirement is
    /// <c>alignof(T)</c>, never <c>sizeof(T)</c>: <c>Buffer</c> is 8 bytes but only 4-byte aligned,
    /// so demanding 8 would reject legal files. The test is meaningful because the buffers this
    /// reader is handed are pinned or native and 64-byte aligned; an unpinned managed array can in
    /// principle be moved by the garbage collector after the check.
    /// </para>
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The element bytes escape the buffer, or their address is not a multiple of
    /// <typeparamref name="T"/>'s natural alignment. Reinterpretation is the point of this method,
    /// so it never silently falls back to a copy; use
    /// <see cref="TryGetStructVector{T}(int, out ReadOnlySpan{T})"/> to handle the misaligned case.
    /// </exception>
    public ReadOnlySpan<T> GetStructVector<T>(int fieldId) where T : unmanaged
    {
        if (!TryGetStructVectorCore<T>(fieldId, out ReadOnlySpan<T> values, out bool misaligned))
        {
            if (misaligned)
            {
                ThrowMisaligned(fieldId, FlatBufferAccess.AlignmentOf<T>());
            }

            return default;
        }

        return values;
    }

    /// <summary>
    /// Non-throwing counterpart of <see cref="GetStructVector{T}(int)"/> for the alignment case
    /// only: returns <see langword="false"/> when the element bytes cannot be reinterpreted in
    /// place. An absent field returns <see langword="true"/> with an empty span, and a range that
    /// escapes the buffer still throws.
    /// </summary>
    public bool TryGetStructVector<T>(int fieldId, out ReadOnlySpan<T> values) where T : unmanaged
    {
        bool ok = TryGetStructVectorCore<T>(fieldId, out values, out bool misaligned);
        return ok || !misaligned;
    }

    /// <summary>
    /// Reads an inline FlatBuffers <c>struct</c> field stored in the table body. Absent yields
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>The value is copied out with an unaligned load, so no alignment rule applies.</remarks>
    public bool TryGetStruct<T>(int fieldId, out T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (!TryGetFieldPos(fieldId, size, out int pos))
        {
            value = default;
            return false;
        }

        // TryGetFieldPos already proved slot + size <= table_size and table_pos + table_size <=
        // length; re-checking here costs one predictable branch and keeps the unsafe read local.
        FlatBufferAccess.CheckRange(_buffer, pos, size, "inline struct");
        value = Unsafe.ReadUnaligned<T>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        return true;
    }

    /// <summary>
    /// Reads a vector of tables or strings. Absent yields a vector whose <c>Count</c> is 0.
    /// </summary>
    public FlatBufferVector GetVector(int fieldId)
    {
        if (!TryGetFieldPos(fieldId, sizeof(uint), out int pos))
        {
            return default;
        }

        return new FlatBufferVector(_buffer, ResolveOffset(_buffer, pos), _depth, ref _tableBudget);
    }

    /// <summary>
    /// Resolves the position of the value of <paramref name="fieldId"/>, or returns
    /// <see langword="false"/> when the field is absent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetFieldPos(int fieldId, int width, out int pos)
    {
        // vtable = [u16 vtable_size][u16 table_size][u16 slots...]. Signed arithmetic on purpose:
        // for the null table _vtableSize is 0, slotCount is -2, and every field id is absent.
        int slotCount = (_vtableSize - 4) >> 1;
        if (fieldId < 0 || fieldId >= slotCount)
        {
            pos = 0;
            return false;
        }

        int slot = FlatBufferAccess.ReadUInt16(_buffer, _vtablePos + 4 + (fieldId * 2));
        if (slot == 0)
        {
            pos = 0;
            return false;
        }

        // The first 4 bytes of a table are its vtable soffset, and the value must fit inside the
        // table body: a slot past table_size is malformed.
        if (slot < 4 || slot > _tableSize - width)
        {
            ThrowSlot(fieldId, slot, width, _tableSize);
        }

        pos = _tablePos + slot;
        return true;
    }

    /// <summary>Follows a forward uoffset stored at <paramref name="pos"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveOffset(ReadOnlySpan<byte> buffer, int pos)
    {
        uint relative = FlatBufferAccess.ReadUInt32(buffer, pos);
        long target = (long)pos + relative;
        // uoffsets are unsigned and strictly forward; the target always begins with a 4-byte
        // header (a table's soffset, or a vector's/string's length prefix).
        if (relative == 0 || target > buffer.Length - 4)
        {
            ThrowUOffset(pos, relative, buffer.Length);
        }

        return (int)target;
    }

    /// <summary>Reads a length-prefixed UTF-8 string at <paramref name="pos"/>, excluding its NUL.</summary>
    internal static ReadOnlySpan<byte> ReadString(ReadOnlySpan<byte> buffer, int pos)
    {
        uint length = FlatBufferAccess.ReadUInt32(buffer, pos);
        // The trailing NUL is not counted by the length prefix but must be present, so the range
        // that has to fit is length + 1.
        FlatBufferAccess.CheckRange(buffer, (long)pos + 4, (long)length + 1, "string");
        if (buffer[pos + 4 + (int)length] != 0)
        {
            ThrowStringTerminator(pos, length);
        }

        return buffer.Slice(pos + 4, (int)length);
    }

    private bool TryGetStructVectorCore<T>(int fieldId, out ReadOnlySpan<T> values, out bool misaligned)
        where T : unmanaged
    {
        misaligned = false;
        if (!TryGetFieldPos(fieldId, sizeof(uint), out int pos))
        {
            values = default;
            return true;
        }

        int vectorPos = ResolveOffset(_buffer, pos);
        uint count = FlatBufferAccess.ReadUInt32(_buffer, vectorPos);
        long elements = (long)vectorPos + 4;
        // 64-bit multiply: uint.MaxValue elements of a 16-byte struct is 64 GiB, which must be
        // rejected, not wrapped.
        long bytes = (long)count * Unsafe.SizeOf<T>();
        FlatBufferAccess.CheckRange(_buffer, elements, bytes, "struct vector");

        int alignment = FlatBufferAccess.AlignmentOf<T>();
        // An empty vector has nothing to reinterpret, so its element address is irrelevant: a
        // zero-length vector must never be rejected for alignment.
        if (alignment > 1 && bytes > 0)
        {
            nuint address = ElementAddress(_buffer, (int)elements);
            if ((address & (nuint)(alignment - 1)) != 0)
            {
                misaligned = true;
                values = default;
                return false;
            }
        }

        values = MemoryMarshal.Cast<byte, T>(_buffer.Slice((int)elements, (int)bytes));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe nuint ElementAddress(ReadOnlySpan<byte> buffer, int offset) =>
        (nuint)Unsafe.AsPointer(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), (nint)(uint)offset));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTableBudget() =>
        throw new VortexFormatException(
            $"FlatBuffers traversal visited more than {VortexLimits.MaxFlatBufferTables} tables. " +
            "Forward-only uoffsets exclude cycles but not sharing, so a buffer whose children are " +
            "shared between parents can cost exponentially more work than its size suggests.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRootTruncated(int length) =>
        ThrowHelper.ThrowTruncated("FlatBuffers root offset", 4, length);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRootOffset(uint rootOffset, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers root offset {rootOffset} is not a valid forward reference into a " +
            $"{length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowVTableOutOfBounds(int tablePos, int soffset, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers table at {tablePos} has vtable soffset {soffset}, which resolves outside " +
            $"the {length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowVTableSize(int vtableSize, int vtablePos, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers vtable at {vtablePos} declares {vtableSize} bytes, which is shorter than " +
            $"its 4-byte header or escapes the {length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTableSize(int tableSize, int tablePos, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers table at {tablePos} declares {tableSize} bytes, which is shorter than its " +
            $"4-byte header or escapes the {length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSlot(int fieldId, int slot, int width, int tableSize) =>
        throw new VortexFormatException(
            $"FlatBuffers field {fieldId} has vtable slot {slot}, so its {width}-byte value escapes " +
            $"the {tableSize}-byte table body.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUOffset(int pos, uint relative, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers uoffset {relative} at {pos} is zero or points outside the {length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStringTerminator(int pos, uint length) =>
        throw new VortexFormatException(
            $"FlatBuffers string at {pos} of length {length} is not NUL-terminated.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMisaligned(int fieldId, int alignment) =>
        throw new VortexFormatException(
            $"FlatBuffers struct vector in field {fieldId} is not {alignment}-byte aligned, so it " +
            "cannot be reinterpreted in place.");
}
