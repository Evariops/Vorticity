using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Types;

/// <summary>One node of a <see cref="DTypeArena"/>. Payload fields are read per <see cref="Kind"/>.</summary>
internal struct DTypeNode
{
    /// <summary>Union discriminant.</summary>
    public DTypeKind Kind;

    /// <summary>
    /// Nullability as stored. <see cref="DTypeKind.Null"/> is normalised to
    /// <see cref="Nullability.Nullable"/>; <see cref="DTypeKind.Extension"/> mirrors its storage
    /// dtype, because neither carries a <c>nullable</c> field on the wire.
    /// </summary>
    public Nullability Nullability;

    /// <summary>Primitive only.</summary>
    public PType PType;

    /// <summary>Decimal only.</summary>
    public byte Precision;

    /// <summary>Decimal only.</summary>
    public sbyte Scale;

    /// <summary>Map only.</summary>
    public bool KeysSorted;

    /// <summary>FixedSizeList only.</summary>
    public uint FixedSize;

    /// <summary>Start of this node's run in the arena's child-index array.</summary>
    public int ChildStart;

    /// <summary>Number of children. Also the field count for Struct/Union.</summary>
    public int ChildCount;

    /// <summary>Struct/Union: start of this node's run in the arena's field-name-handle array.</summary>
    public int NameStart;

    /// <summary>Union: start of this node's run in the arena's type-id array.</summary>
    public int TypeIdStart;

    /// <summary>Extension: start of the metadata bytes.</summary>
    public int MetaStart;

    /// <summary>Extension: length of the metadata bytes.</summary>
    public int MetaLength;

    /// <summary>Extension: interned handle of the extension id.</summary>
    public int NameHandle;

    /// <summary>1 for a leaf, <c>1 + max(child depth)</c> otherwise. Capped at construction.</summary>
    public int Depth;

    /// <summary>Cached structural hash. Identical for structurally equal nodes in any arena.</summary>
    public int Hash;
}

/// <summary>One interned name: a slice of the arena's name-byte array plus its cached hash.</summary>
internal struct DTypeNameEntry
{
    public int Start;
    public int Length;
    public int Hash;
}

/// <summary>
/// Arena of DType nodes. A <see cref="DType"/> is a (arena, index) handle, so a wide schema
/// produces no objects beyond this arena's own growable arrays: the nodes themselves, and beside
/// them the child indices, the struct and union field-name handles, the union type ids, the
/// extension metadata bytes and the interned name bytes, each run contiguous per node.
/// </summary>
/// <remarks>
/// <para>
/// Two hash tables make the arena canonical rather than merely compact. One interns names, so
/// equal UTF-8 bytes always yield the same handle and a field-name comparison is an int
/// comparison; the other deduplicates whole nodes, so within one arena "structurally equal" and
/// "same index" coincide, which turns same-arena equality into an int compare and keeps a
/// 1000-column schema of i32 at two nodes rather than 1001. Neither allocates per lookup: both are
/// open-addressed int arrays with linear probing.
/// </para>
/// <para>Not thread-safe for mutation; safe for concurrent reads once fully built.</para>
/// <para>
/// Spans handed out by <see cref="GetName"/>, <see cref="DType.GetFieldNameUtf8"/> and
/// <see cref="DType.ExtensionMetadata"/> point into arena-owned arrays. They are invalidated by a
/// later mutation that grows the array they point into, and by <see cref="Clear"/>. Copy before
/// retaining across construction calls.
/// </para>
/// </remarks>
public sealed class DTypeArena
{
    /// <summary>
    /// Maximum decimal precision the format admits. Precision 39 to 76 selects 256-bit storage,
    /// so rejecting anything above 38 would refuse legal files.
    /// </summary>
    public const int MaxDecimalPrecision = 76;

    /// <summary>Minimum decimal precision. A precision of zero cannot represent any digit.</summary>
    public const int MinDecimalPrecision = 1;

    /// <summary>
    /// Maximum decimal scale. There is no lower bound beyond <see cref="sbyte"/>: a negative scale
    /// means digits before the point, and the format does not constrain it.
    /// </summary>
    public const int MaxDecimalScale = 76;

    private const int MinBuckets = 16;

    /// <summary>
    /// Node pairs a cross-arena comparison visits before it starts memoising. Sized so that no
    /// realistic schema ever reaches it — a 65 000-node comparison is already far past what a
    /// schema holds — and small enough that the exponential a shared-child DAG would otherwise
    /// cost is cut off after microseconds. See <see cref="StructurallyEqual(DTypeArena, int, DTypeArena, int)"/>.
    /// </summary>
    private const int EqualityVisitBudget = 1 << 16;

    private DTypeNode[] _nodes;
    private int _nodeCount;
    private int[] _nodeBuckets;

    private int[] _children;
    private int _childCount;

    private int[] _fieldNames;
    private int _fieldNameCount;

    private byte[] _typeIds;
    private int _typeIdCount;

    private byte[] _meta;
    private int _metaCount;

    private byte[] _nameBytes;
    private int _nameByteCount;
    private DTypeNameEntry[] _nameEntries;
    private int _nameCount;
    private int[] _nameBuckets;

    private int _generation;

    /// <summary>Creates an empty arena.</summary>
    /// <param name="initialCapacity">Hint for the initial node capacity. Must not be negative.</param>
    public DTypeArena(int initialCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        int cap = Math.Max(initialCapacity, 4);
        _nodes = new DTypeNode[cap];
        _nodeBuckets = new int[NextPowerOfTwo(Math.Max(cap * 2, MinBuckets))];
        _children = new int[cap];
        _fieldNames = new int[cap];
        _typeIds = new byte[cap];
        _meta = new byte[MinBuckets];
        _nameBytes = new byte[cap * 8];
        _nameEntries = new DTypeNameEntry[cap];
        _nameBuckets = new int[NextPowerOfTwo(Math.Max(cap * 2, MinBuckets))];
    }

    /// <summary>Number of distinct nodes currently held. Structurally equal nodes share one entry.</summary>
    public int NodeCount => _nodeCount;

    /// <summary>Number of distinct interned names currently held.</summary>
    public int NameCount => _nameCount;

    /// <summary>
    /// Counts how many times this arena has been cleared. Every <see cref="DType"/> carries the
    /// value it was issued under, so a handle held across a <see cref="Clear"/> is recognised
    /// instead of silently reading whatever node now occupies its index.
    /// </summary>
    internal int Generation => _generation;

    /// <summary>
    /// Drops every node and every interned name. Every previously issued <see cref="DType"/> handle
    /// is dead: reading one throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Clear()
    {
        _generation++;
        _nodeCount = 0;
        _childCount = 0;
        _fieldNameCount = 0;
        _typeIdCount = 0;
        _metaCount = 0;
        _nameByteCount = 0;
        _nameCount = 0;
        Array.Clear(_nodeBuckets);
        Array.Clear(_nameBuckets);
    }

    // ---------------------------------------------------------------- name interning

    /// <summary>
    /// Interns a UTF-8 name and returns its handle. Equal bytes always return the same handle, and
    /// nothing is allocated to look one up: parsers intern directly from the file's bytes without
    /// materialising a <see cref="string"/>.
    /// </summary>
    public int InternName(ReadOnlySpan<byte> utf8)
    {
        int hash = HashBytes(utf8);
        if (TryLookupName(utf8, hash, out int existing))
        {
            return existing;
        }

        Ensure(ref _nameBytes, _nameByteCount, utf8.Length);
        utf8.CopyTo(_nameBytes.AsSpan(_nameByteCount));
        Ensure(ref _nameEntries, _nameCount, 1);
        _nameEntries[_nameCount] = new DTypeNameEntry
        {
            Start = _nameByteCount,
            Length = utf8.Length,
            Hash = hash,
        };
        _nameByteCount += utf8.Length;
        int handle = _nameCount++;

        if ((long)_nameCount * 4 >= (long)_nameBuckets.Length * 3)
        {
            RehashNames();
        }
        else
        {
            InsertNameBucket(handle);
        }

        return handle;
    }

    /// <summary>Interns a name given as a <see cref="string"/>. Equivalent to interning its UTF-8 bytes.</summary>
    public int InternName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> buffer = new Scratch<byte>(Encoding.UTF8.GetByteCount(name), stack);
        int written = Encoding.UTF8.GetBytes(name, buffer.Span);
        return InternName(buffer.Span[..written]);
    }

    /// <summary>Returns the UTF-8 bytes behind an interned name handle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The handle was not issued by this arena.</exception>
    public ReadOnlySpan<byte> GetName(int nameHandle)
    {
        if ((uint)nameHandle >= (uint)_nameCount)
        {
            ThrowBadNameHandle(nameHandle);
        }

        return NameSpan(nameHandle);
    }

    /// <summary>
    /// Looks up an already interned name without inserting it. Returns <c>false</c> when the arena
    /// has never seen these bytes, which lets <see cref="DType.IndexOfField(ReadOnlySpan{byte})"/>
    /// answer "absent" without mutating the arena.
    /// </summary>
    internal bool TryGetName(ReadOnlySpan<byte> utf8, out int handle) =>
        TryLookupName(utf8, HashBytes(utf8), out handle);

    internal ReadOnlySpan<byte> NameSpan(int handle)
    {
        ref DTypeNameEntry e = ref _nameEntries[handle];
        return _nameBytes.AsSpan(e.Start, e.Length);
    }

    private bool TryLookupName(ReadOnlySpan<byte> utf8, int hash, out int handle)
    {
        int mask = _nameBuckets.Length - 1;
        int i = hash & mask;
        while (true)
        {
            int slot = _nameBuckets[i];
            if (slot == 0)
            {
                handle = -1;
                return false;
            }

            int candidate = slot - 1;
            ref DTypeNameEntry e = ref _nameEntries[candidate];
            if (e.Hash == hash && _nameBytes.AsSpan(e.Start, e.Length).SequenceEqual(utf8))
            {
                handle = candidate;
                return true;
            }

            i = (i + 1) & mask;
        }
    }

    private void InsertNameBucket(int handle)
    {
        int mask = _nameBuckets.Length - 1;
        int i = _nameEntries[handle].Hash & mask;
        while (_nameBuckets[i] != 0)
        {
            i = (i + 1) & mask;
        }

        _nameBuckets[i] = handle + 1;
    }

    private void RehashNames()
    {
        _nameBuckets = new int[_nameBuckets.Length * 2];
        for (int h = 0; h < _nameCount; h++)
        {
            InsertNameBucket(h);
        }
    }

    // ---------------------------------------------------------------- leaf factories

    /// <summary>
    /// The all-null dtype. A Null dtype carries no <c>nullable</c> field on the wire, so
    /// nullability is not part of its identity: the argument is accepted for call-site symmetry
    /// and the node is always nullable. Storing it instead would make a round trip through the
    /// file format lossy.
    /// </summary>
    public DType Null(Nullability nullability)
    {
        CheckNullability(nullability);
        return Leaf(DTypeKind.Null, Nullability.Nullable);
    }

    /// <summary>The boolean dtype.</summary>
    public DType Bool(Nullability nullability) => Leaf(DTypeKind.Bool, nullability);

    /// <summary>A fixed-width primitive dtype.</summary>
    /// <exception cref="VortexFormatException"><paramref name="ptype"/> is not a defined tag.</exception>
    public DType Primitive(PType ptype, Nullability nullability)
    {
        if (!PTypeExtensions.IsDefined(ptype))
        {
            ThrowFormat($"PType tag {(byte)ptype} is not defined; the schema defines 0..{PTypeExtensions.MaxPType}.");
        }

        CheckNullability(nullability);
        DTypeNode n = NewNode(DTypeKind.Primitive, nullability);
        n.PType = ptype;
        return Commit(ref n, _childCount, _fieldNameCount, _typeIdCount, _metaCount);
    }

    /// <summary>
    /// A fixed-point decimal dtype.
    /// </summary>
    /// <param name="precision">
    /// Total number of significant digits, 1..<see cref="MaxDecimalPrecision"/> (76). The storage
    /// width follows it — 1-2 to i8, 3-4 to i16, 5-9 to i32, 10-18 to i64, 19-38 to i128,
    /// 39-76 to i256 — so an out-of-range precision would select a storage width that does not
    /// exist. Rejecting anything above 38 would refuse legal files.
    /// </param>
    /// <param name="scale">
    /// Digits after the point. Bounded above by <see cref="MaxDecimalScale"/>, and by
    /// <paramref name="precision"/> only when positive: the <c>scale &lt;= precision</c> check
    /// applies only to a positive scale, so a negative scale (digits before the point) is legal
    /// down to <see cref="sbyte.MinValue"/>.
    /// </param>
    /// <param name="nullability">Whether the dtype admits nulls.</param>
    /// <exception cref="VortexFormatException">Precision or scale is out of range.</exception>
    public DType Decimal(byte precision, sbyte scale, Nullability nullability)
    {
        if (precision < MinDecimalPrecision || precision > MaxDecimalPrecision)
        {
            ThrowFormat(
                $"Decimal precision {precision} is outside the supported range " +
                $"[{MinDecimalPrecision}, {MaxDecimalPrecision}].");
        }

        if (scale > MaxDecimalScale || (scale > 0 && scale > precision))
        {
            ThrowFormat(
                $"Decimal scale {scale} is illegal for precision {precision}: scale must be " +
                $"at most {MaxDecimalScale}, and at most the precision when positive.");
        }

        CheckNullability(nullability);
        DTypeNode n = NewNode(DTypeKind.Decimal, nullability);
        n.Precision = precision;
        n.Scale = scale;
        return Commit(ref n, _childCount, _fieldNameCount, _typeIdCount, _metaCount);
    }

    /// <summary>The UTF-8 string dtype.</summary>
    public DType Utf8(Nullability nullability) => Leaf(DTypeKind.Utf8, nullability);

    /// <summary>The opaque-bytes dtype.</summary>
    public DType Binary(Nullability nullability) => Leaf(DTypeKind.Binary, nullability);

    /// <summary>The self-describing variant dtype (RFC 0015).</summary>
    public DType Variant(Nullability nullability) => Leaf(DTypeKind.Variant, nullability);

    // ---------------------------------------------------------------- composite factories

    /// <summary>
    /// A struct dtype over already-interned field names.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// <paramref name="nameHandles"/> and <paramref name="fields"/> differ in length, or the
    /// resulting depth exceeds <see cref="VortexLimits.MaxDTypeDepth"/>. The format requires the
    /// two lengths to match but nothing enforces it in the bytes, so both are reachable from a
    /// file.
    /// </exception>
    /// <exception cref="ArgumentException">A field belongs to a different arena, or is default.</exception>
    public DType Struct(ReadOnlySpan<int> nameHandles, ReadOnlySpan<DType> fields, Nullability nullability)
    {
        if (nameHandles.Length != fields.Length)
        {
            ThrowFormat(
                $"Struct names.len() = {nameHandles.Length} does not match dtypes.len() = {fields.Length}.");
        }

        CheckNullability(nullability);
        int depth = ValidateChildren(fields);

        int savedChild = _childCount;
        int savedName = _fieldNameCount;
        DTypeNode n = NewNode(DTypeKind.Struct, nullability);
        n.Depth = depth;
        AppendNameHandles(nameHandles);
        AppendChildren(fields);
        n.ChildCount = fields.Length;
        return Commit(ref n, savedChild, savedName, _typeIdCount, _metaCount);
    }

    /// <summary>A struct dtype whose field names are interned on the fly.</summary>
    /// <inheritdoc cref="Struct(ReadOnlySpan{int}, ReadOnlySpan{DType}, Nullability)" path="/exception"/>
    public DType Struct(ReadOnlySpan<string> names, ReadOnlySpan<DType> fields, Nullability nullability)
    {
        if (names.Length != fields.Length)
        {
            ThrowFormat(
                $"Struct names.len() = {names.Length} does not match dtypes.len() = {fields.Length}.");
        }

        CheckNullability(nullability);
        int depth = ValidateChildren(fields);

        int savedChild = _childCount;
        int savedName = _fieldNameCount;
        DTypeNode n = NewNode(DTypeKind.Struct, nullability);
        n.Depth = depth;
        AppendInternedNames(names);
        AppendChildren(fields);
        n.ChildCount = fields.Length;
        return Commit(ref n, savedChild, savedName, _typeIdCount, _metaCount);
    }

    /// <summary>A variable-length list dtype.</summary>
    /// <exception cref="ArgumentException"><paramref name="elementType"/> belongs to another arena.</exception>
    /// <exception cref="VortexFormatException">The resulting depth exceeds the cap.</exception>
    public DType List(DType elementType, Nullability nullability)
    {
        CheckNullability(nullability);
        int depth = ChildDepth(elementType) + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "DType");

        int savedChild = _childCount;
        DTypeNode n = NewNode(DTypeKind.List, nullability);
        n.Depth = depth;
        AppendChild(elementType);
        n.ChildCount = 1;
        return Commit(ref n, savedChild, _fieldNameCount, _typeIdCount, _metaCount);
    }

    /// <summary>A fixed-length list dtype.</summary>
    /// <exception cref="ArgumentException"><paramref name="elementType"/> belongs to another arena.</exception>
    /// <exception cref="VortexFormatException">The resulting depth exceeds the cap.</exception>
    public DType FixedSizeList(DType elementType, uint size, Nullability nullability)
    {
        CheckNullability(nullability);
        int depth = ChildDepth(elementType) + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "DType");

        int savedChild = _childCount;
        DTypeNode n = NewNode(DTypeKind.FixedSizeList, nullability);
        n.Depth = depth;
        n.FixedSize = size;
        AppendChild(elementType);
        n.ChildCount = 1;
        return Commit(ref n, savedChild, _fieldNameCount, _typeIdCount, _metaCount);
    }

    /// <summary>
    /// An extension dtype. An extension carries no <c>nullable</c> field on the wire: its
    /// nullability is exactly its storage dtype's, which is why this factory takes no
    /// <see cref="Nullability"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="storageType"/> belongs to another arena.</exception>
    /// <exception cref="VortexFormatException">The resulting depth exceeds the cap.</exception>
    public DType Extension(ReadOnlySpan<byte> idUtf8, DType storageType, ReadOnlySpan<byte> metadata)
    {
        int depth = ChildDepth(storageType) + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "DType");

        int nameHandle = InternName(idUtf8);
        int savedChild = _childCount;
        int savedMeta = _metaCount;

        // Nullability is derived, never stored independently, so the two can never disagree.
        DTypeNode n = NewNode(DTypeKind.Extension, _nodes[storageType.NodeIndex].Nullability);
        n.Depth = depth;
        n.NameHandle = nameHandle;
        if (metadata.Length != 0)
        {
            Ensure(ref _meta, _metaCount, metadata.Length);
            // `metadata` may alias _meta; the span still refers to the pre-resize array, whose
            // contents Array.Resize copied forward, so the copy is well defined either way.
            metadata.CopyTo(_meta.AsSpan(_metaCount));
            n.MetaStart = _metaCount;
            n.MetaLength = metadata.Length;
            _metaCount += metadata.Length;
        }

        AppendChild(storageType);
        n.ChildCount = 1;
        return Commit(ref n, savedChild, _fieldNameCount, _typeIdCount, savedMeta);
    }

    /// <summary>An extension dtype whose id is given as a <see cref="string"/>.</summary>
    /// <inheritdoc cref="Extension(ReadOnlySpan{byte}, DType, ReadOnlySpan{byte})" path="/exception"/>
    public DType Extension(string id, DType storageType, ReadOnlySpan<byte> metadata)
    {
        ArgumentNullException.ThrowIfNull(id);

        // Encode into a scratch buffer rather than interning first: a rejected storage dtype must
        // not leave the id behind in the intern table.
        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> buffer = new Scratch<byte>(Encoding.UTF8.GetByteCount(id), stack);
        int written = Encoding.UTF8.GetBytes(id, buffer.Span);
        return Extension(buffer.Span[..written], storageType, metadata);
    }

    /// <summary>
    /// A tagged union dtype.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// <paramref name="nameHandles"/>, <paramref name="fields"/> and <paramref name="typeIds"/> do
    /// not all have the same length, or the resulting depth exceeds the cap. The format requires
    /// the three lengths to match but nothing enforces it in the bytes, so a file can carry the
    /// mismatch.
    /// </exception>
    /// <exception cref="ArgumentException">A field belongs to a different arena, or is default.</exception>
    public DType Union(
        ReadOnlySpan<int> nameHandles,
        ReadOnlySpan<DType> fields,
        ReadOnlySpan<byte> typeIds,
        Nullability nullability)
    {
        if (nameHandles.Length != fields.Length)
        {
            ThrowFormat(
                $"Union names.len() = {nameHandles.Length} does not match dtypes.len() = {fields.Length}.");
        }

        if (typeIds.Length != fields.Length)
        {
            ThrowFormat(
                $"Union type_ids.len() = {typeIds.Length} does not match dtypes.len() = {fields.Length}.");
        }

        CheckNullability(nullability);
        int depth = ValidateChildren(fields);

        int savedChild = _childCount;
        int savedName = _fieldNameCount;
        int savedTypeId = _typeIdCount;
        DTypeNode n = NewNode(DTypeKind.Union, nullability);
        n.Depth = depth;
        AppendNameHandles(nameHandles);
        Ensure(ref _typeIds, _typeIdCount, typeIds.Length);
        typeIds.CopyTo(_typeIds.AsSpan(_typeIdCount));
        n.TypeIdStart = _typeIdCount;
        _typeIdCount += typeIds.Length;
        AppendChildren(fields);
        n.ChildCount = fields.Length;
        return Commit(ref n, savedChild, savedName, savedTypeId, _metaCount);
    }

    /// <summary>A map dtype. Children are stored key first, then value, matching the wire order.</summary>
    /// <exception cref="ArgumentException">A child belongs to a different arena, or is default.</exception>
    /// <exception cref="VortexFormatException">The resulting depth exceeds the cap.</exception>
    public DType Map(DType keyType, DType valueType, bool keysSorted, Nullability nullability)
    {
        CheckNullability(nullability);
        int depth = Math.Max(ChildDepth(keyType), ChildDepth(valueType)) + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "DType");

        int savedChild = _childCount;
        DTypeNode n = NewNode(DTypeKind.Map, nullability);
        n.Depth = depth;
        n.KeysSorted = keysSorted;
        AppendChild(keyType);
        AppendChild(valueType);
        n.ChildCount = 2;
        return Commit(ref n, savedChild, _fieldNameCount, _typeIdCount, _metaCount);
    }

    // ---------------------------------------------------------------- node access

    internal ref readonly DTypeNode NodeRef(int index, int generation)
    {
        CheckGeneration(generation);
        return ref _nodes[index];
    }

    /// <summary>
    /// Refuses a handle issued before the last <see cref="Clear"/>. The index alone cannot tell:
    /// after a clear the arena refills from zero, so a stale handle stays in bounds and names a
    /// node of some unrelated dtype.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckGeneration(int generation)
    {
        if (generation != _generation)
        {
            ThrowStale(generation, _generation);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStale(int held, int current) => throw new InvalidOperationException(
        $"This DType was issued before the arena was cleared (handle generation {held}, arena " +
        $"generation {current}); its node index no longer means anything.");

    internal ReadOnlySpan<byte> MetaSpan(in DTypeNode n) => _meta.AsSpan(n.MetaStart, n.MetaLength);

    internal int ChildIndex(in DTypeNode n, int i) => _children[n.ChildStart + i];

    internal int FieldNameHandle(in DTypeNode n, int i) => _fieldNames[n.NameStart + i];

    internal byte TypeIdAt(in DTypeNode n, int i) => _typeIds[n.TypeIdStart + i];

    internal ReadOnlySpan<int> FieldNameHandles(in DTypeNode n) =>
        _fieldNames.AsSpan(n.NameStart, n.ChildCount);

    /// <summary>
    /// Returns a node structurally identical to <paramref name="index"/> but with the requested
    /// nullability. Children, field names, type ids and metadata are shared by value: only the
    /// top node changes, so the depth is unchanged and no recursion happens.
    /// </summary>
    internal DType CloneWithNullability(int index, Nullability nullability)
    {
        DTypeNode src = _nodes[index];
        DTypeNode n = src;
        n.Nullability = nullability;

        int savedChild = _childCount;
        int savedName = _fieldNameCount;
        int savedTypeId = _typeIdCount;
        int savedMeta = _metaCount;

        n.ChildStart = _childCount;
        if (src.ChildCount > 0)
        {
            Ensure(ref _children, _childCount, src.ChildCount);
            Array.Copy(_children, src.ChildStart, _children, _childCount, src.ChildCount);
            _childCount += src.ChildCount;
        }

        n.NameStart = _fieldNameCount;
        if (src.Kind is DTypeKind.Struct or DTypeKind.Union && src.ChildCount > 0)
        {
            Ensure(ref _fieldNames, _fieldNameCount, src.ChildCount);
            Array.Copy(_fieldNames, src.NameStart, _fieldNames, _fieldNameCount, src.ChildCount);
            _fieldNameCount += src.ChildCount;
        }

        n.TypeIdStart = _typeIdCount;
        if (src.Kind == DTypeKind.Union && src.ChildCount > 0)
        {
            Ensure(ref _typeIds, _typeIdCount, src.ChildCount);
            Array.Copy(_typeIds, src.TypeIdStart, _typeIds, _typeIdCount, src.ChildCount);
            _typeIdCount += src.ChildCount;
        }

        n.MetaStart = _metaCount;
        if (src.MetaLength > 0)
        {
            Ensure(ref _meta, _metaCount, src.MetaLength);
            Array.Copy(_meta, src.MetaStart, _meta, _metaCount, src.MetaLength);
            _metaCount += src.MetaLength;
        }

        return Commit(ref n, savedChild, savedName, savedTypeId, savedMeta);
    }

    /// <summary>
    /// Structural comparison of two nodes, possibly in different arenas. Within one arena the
    /// dedup table makes index equality both necessary and sufficient, so the recursive walk only
    /// runs across arenas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="VortexLimits.MaxDTypeDepth"/> bounds the recursion stack and nothing else. It
    /// does not bound the number of visits: children are node indices and the arena deduplicates
    /// whole nodes, so <c>Struct(["a","b"], [d, d])</c> stores the same child index twice and a
    /// dtype nested that way 64 deep is a 65-node DAG with 2^64 root-to-leaf paths. Walking it per
    /// path never returns, and the <see cref="DTypeNode.Hash"/> early-out cannot help, because two
    /// equal dtypes hash equal by construction.
    /// </para>
    /// <para>
    /// So the visit count is bounded instead: the walk runs allocation-free until it has visited
    /// <see cref="EqualityVisitBudget"/> node pairs, then memoises the pairs it has proven equal
    /// and returns early on a revisit. A mismatch propagates straight out through every frame, so
    /// unequal pairs are never revisited and are not memoised. The dedup table makes a node index
    /// canonical within its arena, so a pair proven equal is equal in every context it is reached
    /// from, which is what makes the memo sound.
    /// </para>
    /// </remarks>
    internal static bool StructurallyEqual(DTypeArena a, int ai, DTypeArena b, int bi)
    {
        if (ReferenceEquals(a, b))
        {
            return ai == bi;
        }

        int budget = EqualityVisitBudget;
        HashSet<long>? memo = null;
        return StructurallyEqual(a, ai, b, bi, ref budget, ref memo);
    }

    private static bool StructurallyEqual(
        DTypeArena a, int ai, DTypeArena b, int bi, ref int budget, ref HashSet<long>? memo)
    {
        long pair = ((long)ai << 32) | (uint)bi;
        if (memo is not null)
        {
            // Asked and added separately: the add is on the far side of the recursion, where only
            // a pair proven equal is remembered. Folding the two into one `Add` would have to
            // remember the pair before proving it.
            if (memo.Contains(pair))
            {
                return true;
            }
        }
        else if (--budget < 0)
        {
            // More visits than any realistic schema needs: the graph is being walked per path, so
            // from here on remember what has already been proven equal.
            memo = new HashSet<long>();
        }

        ref readonly DTypeNode x = ref a._nodes[ai];
        ref readonly DTypeNode y = ref b._nodes[bi];

        if (x.Hash != y.Hash || x.Kind != y.Kind || x.Nullability != y.Nullability ||
            x.ChildCount != y.ChildCount)
        {
            return false;
        }

        switch (x.Kind)
        {
            case DTypeKind.Primitive:
                if (x.PType != y.PType)
                {
                    return false;
                }

                break;

            case DTypeKind.Decimal:
                if (x.Precision != y.Precision || x.Scale != y.Scale)
                {
                    return false;
                }

                break;

            case DTypeKind.FixedSizeList:
                if (x.FixedSize != y.FixedSize)
                {
                    return false;
                }

                break;

            case DTypeKind.Map:
                if (x.KeysSorted != y.KeysSorted)
                {
                    return false;
                }

                break;

            case DTypeKind.Extension:
                if (!a.NameSpan(x.NameHandle).SequenceEqual(b.NameSpan(y.NameHandle)) ||
                    !a.MetaSpan(in x).SequenceEqual(b.MetaSpan(in y)))
                {
                    return false;
                }

                break;

            case DTypeKind.Struct:
                for (int i = 0; i < x.ChildCount; i++)
                {
                    if (!a.NameSpan(a._fieldNames[x.NameStart + i])
                            .SequenceEqual(b.NameSpan(b._fieldNames[y.NameStart + i])))
                    {
                        return false;
                    }
                }

                break;

            case DTypeKind.Union:
                for (int i = 0; i < x.ChildCount; i++)
                {
                    if (a._typeIds[x.TypeIdStart + i] != b._typeIds[y.TypeIdStart + i] ||
                        !a.NameSpan(a._fieldNames[x.NameStart + i])
                            .SequenceEqual(b.NameSpan(b._fieldNames[y.NameStart + i])))
                    {
                        return false;
                    }
                }

                break;

            default:
                break;
        }

        for (int i = 0; i < x.ChildCount; i++)
        {
            if (!StructurallyEqual(
                    a, a._children[x.ChildStart + i], b, b._children[y.ChildStart + i], ref budget, ref memo))
            {
                return false;
            }
        }

        // Re-read: the memo may have been switched on deeper inside the recursion above.
        memo?.Add(pair);
        return true;
    }

    // ---------------------------------------------------------------- construction plumbing

    private DType Leaf(DTypeKind kind, Nullability nullability)
    {
        CheckNullability(nullability);
        DTypeNode n = NewNode(kind, nullability);
        return Commit(ref n, _childCount, _fieldNameCount, _typeIdCount, _metaCount);
    }

    private DTypeNode NewNode(DTypeKind kind, Nullability nullability) => new()
    {
        Kind = kind,
        Nullability = nullability,
        ChildStart = _childCount,
        NameStart = _fieldNameCount,
        TypeIdStart = _typeIdCount,
        MetaStart = _metaCount,
        Depth = 1,
    };

    /// <summary>
    /// Validates every child's provenance and returns the depth the parent would have. Runs before
    /// anything is appended so a rejected dtype leaves no garbage in the side arrays.
    /// </summary>
    private int ValidateChildren(ReadOnlySpan<DType> children)
    {
        int depth = 0;
        for (int i = 0; i < children.Length; i++)
        {
            int d = ChildDepth(children[i]);
            if (d > depth)
            {
                depth = d;
            }
        }

        depth++;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "DType");
        return depth;
    }

    private int ChildDepth(DType child)
    {
        if (!ReferenceEquals(child.ArenaOrNull, this))
        {
            ThrowForeignChild(child);
        }

        return _nodes[child.NodeIndex].Depth;
    }

    private void AppendChild(DType child)
    {
        Ensure(ref _children, _childCount, 1);
        _children[_childCount++] = child.NodeIndex;
    }

    private void AppendChildren(ReadOnlySpan<DType> children)
    {
        Ensure(ref _children, _childCount, children.Length);
        for (int i = 0; i < children.Length; i++)
        {
            _children[_childCount + i] = children[i].NodeIndex;
        }

        _childCount += children.Length;
    }

    private void AppendNameHandles(ReadOnlySpan<int> nameHandles)
    {
        Ensure(ref _fieldNames, _fieldNameCount, nameHandles.Length);
        for (int i = 0; i < nameHandles.Length; i++)
        {
            int h = nameHandles[i];
            if ((uint)h >= (uint)_nameCount)
            {
                ThrowBadNameHandle(h);
            }

            _fieldNames[_fieldNameCount + i] = h;
        }

        _fieldNameCount += nameHandles.Length;
    }

    private void AppendInternedNames(ReadOnlySpan<string> names)
    {
        // Interning can grow _nameBytes but never _fieldNames, so reserving first is safe.
        Ensure(ref _fieldNames, _fieldNameCount, names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            string? name = names[i];
            if (name is null)
            {
                ThrowNullFieldName(i);
            }

            _fieldNames[_fieldNameCount + i] = InternName(name);
        }

        _fieldNameCount += names.Length;
    }

    private DType Commit(ref DTypeNode node, int savedChild, int savedFieldName, int savedTypeId, int savedMeta)
    {
        node.Hash = ComputeHash(in node);

        int mask = _nodeBuckets.Length - 1;
        int i = node.Hash & mask;
        while (true)
        {
            int slot = _nodeBuckets[i];
            if (slot == 0)
            {
                break;
            }

            int candidate = slot - 1;
            if (_nodes[candidate].Hash == node.Hash && LocallyEqual(in _nodes[candidate], in node))
            {
                // Duplicate: roll the side arrays back so the arena stays canonical and compact.
                _childCount = savedChild;
                _fieldNameCount = savedFieldName;
                _typeIdCount = savedTypeId;
                _metaCount = savedMeta;
                return new DType(this, candidate);
            }

            i = (i + 1) & mask;
        }

        Ensure(ref _nodes, _nodeCount, 1);
        _nodes[_nodeCount] = node;
        int index = _nodeCount++;
        if ((long)_nodeCount * 4 >= (long)_nodeBuckets.Length * 3)
        {
            RehashNodes();
        }
        else
        {
            _nodeBuckets[i] = index + 1;
        }

        return new DType(this, index);
    }

    private void InsertNodeBucket(int index)
    {
        int mask = _nodeBuckets.Length - 1;
        int i = _nodes[index].Hash & mask;
        while (_nodeBuckets[i] != 0)
        {
            i = (i + 1) & mask;
        }

        _nodeBuckets[i] = index + 1;
    }

    private void RehashNodes()
    {
        _nodeBuckets = new int[_nodeBuckets.Length * 2];
        for (int i = 0; i < _nodeCount; i++)
        {
            InsertNodeBucket(i);
        }
    }

    /// <summary>
    /// Same-arena node comparison used by the dedup table. Children are already canonical, so
    /// comparing child indices is exact and no recursion is needed.
    /// </summary>
    private bool LocallyEqual(in DTypeNode a, in DTypeNode b)
    {
        if (a.Kind != b.Kind || a.Nullability != b.Nullability || a.PType != b.PType ||
            a.Precision != b.Precision || a.Scale != b.Scale || a.KeysSorted != b.KeysSorted ||
            a.FixedSize != b.FixedSize || a.NameHandle != b.NameHandle ||
            a.ChildCount != b.ChildCount || a.MetaLength != b.MetaLength)
        {
            return false;
        }

        if (!_children.AsSpan(a.ChildStart, a.ChildCount)
                .SequenceEqual(_children.AsSpan(b.ChildStart, b.ChildCount)))
        {
            return false;
        }

        if (a.Kind is DTypeKind.Struct or DTypeKind.Union &&
            !_fieldNames.AsSpan(a.NameStart, a.ChildCount)
                .SequenceEqual(_fieldNames.AsSpan(b.NameStart, b.ChildCount)))
        {
            return false;
        }

        if (a.Kind == DTypeKind.Union &&
            !_typeIds.AsSpan(a.TypeIdStart, a.ChildCount)
                .SequenceEqual(_typeIds.AsSpan(b.TypeIdStart, b.ChildCount)))
        {
            return false;
        }

        return a.MetaLength == 0 ||
               _meta.AsSpan(a.MetaStart, a.MetaLength).SequenceEqual(_meta.AsSpan(b.MetaStart, b.MetaLength));
    }

    /// <summary>
    /// Structural hash of a node. It depends on nothing but the node's own shape and its children's
    /// cached hashes, so two arenas that built the same dtype produce the same value — which is
    /// what makes cross-arena <see cref="DType.GetHashCode"/> agree with cross-arena equality.
    /// </summary>
    private int ComputeHash(in DTypeNode n)
    {
        HashCode hc = default;
        hc.Add((byte)n.Kind);
        hc.Add((byte)n.Nullability);
        switch (n.Kind)
        {
            case DTypeKind.Primitive:
                hc.Add((byte)n.PType);
                break;

            case DTypeKind.Decimal:
                hc.Add(n.Precision);
                hc.Add(n.Scale);
                break;

            case DTypeKind.FixedSizeList:
                hc.Add(n.FixedSize);
                break;

            case DTypeKind.Map:
                hc.Add(n.KeysSorted ? 1 : 0);
                break;

            case DTypeKind.Extension:
                hc.AddBytes(NameSpan(n.NameHandle));
                hc.AddBytes(_meta.AsSpan(n.MetaStart, n.MetaLength));
                break;

            case DTypeKind.Struct:
                for (int i = 0; i < n.ChildCount; i++)
                {
                    hc.AddBytes(NameSpan(_fieldNames[n.NameStart + i]));
                }

                break;

            case DTypeKind.Union:
                for (int i = 0; i < n.ChildCount; i++)
                {
                    hc.AddBytes(NameSpan(_fieldNames[n.NameStart + i]));
                    hc.Add(_typeIds[n.TypeIdStart + i]);
                }

                break;

            default:
                break;
        }

        for (int i = 0; i < n.ChildCount; i++)
        {
            hc.Add(_nodes[_children[n.ChildStart + i]].Hash);
        }

        return hc.ToHashCode();
    }

    // ---------------------------------------------------------------- small utilities

    private static int HashBytes(ReadOnlySpan<byte> bytes)
    {
        // HashCode is seeded per process, so a hostile file cannot precompute colliding field
        // names to degrade the intern table into a linear scan.
        HashCode hc = default;
        hc.AddBytes(bytes);
        return hc.ToHashCode();
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = MinBuckets;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static void Ensure<T>(ref T[] array, int count, int extra)
    {
        long need = (long)count + extra;
        if (need <= array.Length)
        {
            return;
        }

        const int Ceiling = int.MaxValue - 16;
        if (need > Ceiling)
        {
            ThrowFormat("DType arena exceeded the maximum addressable size.");
        }

        int capacity = array.Length == 0 ? 4 : array.Length;
        while (capacity < need)
        {
            capacity = capacity > Ceiling / 2 ? Ceiling : capacity * 2;
        }

        Array.Resize(ref array, capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckNullability(Nullability nullability)
    {
        // The wire field is a bool, so only 0 and 1 exist; an enum cast can smuggle anything else
        // in and would silently create a node that compares unequal to its own round trip.
        if ((byte)nullability > 1)
        {
            ThrowFormat($"Nullability value {(byte)nullability} is not 0 (NonNullable) or 1 (Nullable).");
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFormat(string message) => throw new VortexFormatException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowForeignChild(DType child) => throw new ArgumentException(
        child.IsDefault
            ? "A default DType cannot be used as a child; it belongs to no arena."
            : "Child DTypes must come from the same DTypeArena as their parent.",
        nameof(child));

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBadNameHandle(int handle) => throw new ArgumentOutOfRangeException(
        nameof(handle), handle, "The name handle was not issued by this DTypeArena.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNullFieldName(int index) => throw new ArgumentNullException(
        "names", $"Field name at index {index} is null.");
}
