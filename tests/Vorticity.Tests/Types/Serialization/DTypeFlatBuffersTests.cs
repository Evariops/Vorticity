// Adversarial tests for the FlatBuffers DType codec. The happy path is covered by
// DTypeEquivalenceTests, which cross-checks every kind against the independent Protobuf codec;
// what is here is the set of things a round trip through our own writer CANNOT catch:
//
//   * the union's two-slot layout (tag at field 0, value at field 1) -- asserted against exact,
//     hand-computed bytes and against the raw vtable, not through our own reader;
//   * every way a hostile buffer can be malformed: truncation, zero length, a zero or overflowing
//     root uoffset, a NONE or undefined tag, a missing required child, mismatched parallel
//     vectors, an undefined PType, nesting past the cap;
//   * the rule that nothing but VortexFormatException ever escapes, whatever the bytes.
using System;
using System.Buffers.Binary;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Types.Serialization;

public sealed class DTypeFlatBuffersTests
{
    // ------------------------------------------------------------------ exact wire layout

    /// <summary>
    /// The whole point of this file. <c>bool?</c> is the smallest dtype with a payload, and these
    /// 40 bytes were computed by hand from the Vortex DType schema and the FlatBuffers layout
    /// rules, not captured from the implementation. If the union's two slots are ever swapped this
    /// is the assertion that fails; a round trip would not, because the writer would swap them too.
    /// </summary>
    [Fact]
    public void BoolNullable_MatchesHandComputedBytes()
    {
        DTypeArena arena = new DTypeArena();
        byte[] actual = DTypeFlatBuffers.Serialize(arena.Bool(Nullability.Nullable));

        byte[] expected =
        [
            // 0x00  root uoffset = 16 -> the DType table at 0x10
            0x10, 0x00, 0x00, 0x00,

            // 0x04  padding to make the finished length a multiple of 8
            0x00, 0x00, 0x00, 0x00,

            // 0x08  vtable of the DType table
            0x08, 0x00,             //   vtable_size = 8  (header + 2 slots => a union is 2 slots)
            0x0A, 0x00,             //   table_size  = 10
            0x09, 0x00,             //   slot[0] = 9  -> the ubyte type tag at 0x10 + 9 = 0x19
            0x04, 0x00,             //   slot[1] = 4  -> the value uoffset at 0x10 + 4 = 0x14

            // 0x10  the DType table
            0x08, 0x00, 0x00, 0x00, //   soffset = 8 -> vtable at 0x10 - 8 = 0x08
            0x0C, 0x00, 0x00, 0x00, //   field 1: uoffset 12 -> the Bool table at 0x14 + 12 = 0x20
            0x00,                   //   padding
            0x02,                   //   field 0: type tag = 2 (Bool)

            // 0x1A  vtable of the Bool table
            0x06, 0x00,             //   vtable_size = 6 (header + 1 slot)
            0x08, 0x00,             //   table_size  = 8
            0x07, 0x00,             //   slot[0] = 7 -> nullable at 0x20 + 7 = 0x27

            // 0x20  the Bool table
            0x06, 0x00, 0x00, 0x00, //   soffset = 6 -> vtable at 0x20 - 6 = 0x1A
            0x00, 0x00, 0x00,       //   padding
            0x01,                   //   field 0: nullable = true
        ];

        Assert.Equal(expected, actual);

        // ... and the bytes really do parse back to what they claim to be.
        DType back = DTypeFlatBuffers.Read(actual, new DTypeArena());
        Assert.Equal(DTypeKind.Bool, back.Kind);
        Assert.True(back.IsNullable);
    }

    /// <summary>
    /// The two-slot rule, read straight out of the vtable rather than through
    /// <see cref="FlatBufferTable"/>: field 0 is a one-byte discriminant, field 1 a uoffset.
    /// </summary>
    [Fact]
    public void UnionTag_IsFieldZero_AndUnionValue_IsFieldOne()
    {
        DTypeArena arena = new DTypeArena();
        byte[] buffer = DTypeFlatBuffers.Serialize(arena.Primitive(PType.I64, Nullability.NonNullable));

        int root = (int)ReadU32(buffer, 0);
        int vtable = root - ReadI32(buffer, root);
        Assert.Equal(2, (ReadU16(buffer, vtable) - 4) / 2);

        int tagSlot = ReadU16(buffer, vtable + 4);
        int valueSlot = ReadU16(buffer, vtable + 6);
        Assert.NotEqual(0, tagSlot);
        Assert.NotEqual(0, valueSlot);

        // Field 0 holds the ubyte discriminant; the schema gives Primitive the tag 3.
        Assert.Equal(3, buffer[root + tagSlot]);

        // Field 1 holds the uoffset to the value table, whose own field 0 is the PType: I64 = 7.
        int valuePos = root + valueSlot;
        int valueTable = valuePos + (int)ReadU32(buffer, valuePos);
        int valueVTable = valueTable - ReadI32(buffer, valueTable);
        Assert.Equal(7, buffer[valueTable + ReadU16(buffer, valueVTable + 4)]);
    }

    /// <summary>
    /// A buffer that puts the value uoffset in slot 0 and the tag in slot 1 -- the off-by-one this
    /// file guards against -- must never decode as the dtype it "meant".
    /// </summary>
    [Fact]
    public void SwappedUnionSlots_DoNotDecodeAsTheIntendedDType()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(0, (byte)PType.I64);
        builder.AddBool(1, false);
        int value = builder.EndTable();

        builder.StartTable();
        builder.AddOffset(0, value);              // WRONG: the value belongs in slot 1
        builder.AddUInt8(1, 3);                   // WRONG: the tag belongs in slot 0
        int root = builder.EndTable();
        byte[] buffer = builder.FinishToArray(root);

        DTypeArena arena = new DTypeArena();
        DType expected = arena.Primitive(PType.I64, Nullability.NonNullable);
        try
        {
            Assert.NotEqual(expected, DTypeFlatBuffers.Read(buffer, new DTypeArena()));
        }
        catch (VortexFormatException)
        {
            // Rejecting it outright is the other acceptable outcome, and the usual one: reading a
            // uoffset's low byte as a tag and a tag byte as a uoffset lands out of bounds.
        }
    }

    /// <summary>
    /// The literal tag bytes the schema assigns, checked on the wire.
    /// </summary>
    /// <remarks>
    /// This is the assertion the cross-codec equivalence property cannot make. Renumber two tags
    /// symmetrically — swap <c>Extension = 9</c> and <c>FixedSizeList = 10</c>, say — and every
    /// round trip still passes, in both codecs independently, because each stays self-consistent.
    /// Only a comparison against the schema's own literals notices, so the numbers below are typed
    /// out rather than derived from anything in the implementation. (Verified by mutation: swapping
    /// those two constants leaves all other tests in this repository green.)
    /// </remarks>
    [Fact]
    public void EveryKind_EmitsItsSchemaTagByte()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);

        AssertTagByte(1, arena.Null(Nullability.Nullable));
        AssertTagByte(2, arena.Bool(Nullability.NonNullable));
        AssertTagByte(3, i32);
        AssertTagByte(4, arena.Decimal(10, 2, Nullability.NonNullable));
        AssertTagByte(5, arena.Utf8(Nullability.NonNullable));
        AssertTagByte(6, arena.Binary(Nullability.NonNullable));
        AssertTagByte(7, arena.Struct(["a"], [i32], Nullability.NonNullable));
        AssertTagByte(8, arena.List(i32, Nullability.NonNullable));
        AssertTagByte(9, arena.Extension("e", i32, []));
        AssertTagByte(10, arena.FixedSizeList(i32, 4, Nullability.NonNullable));
        AssertTagByte(11, arena.Variant(Nullability.NonNullable));
        AssertTagByte(12, arena.Union([arena.InternName("a")], [i32], [0], Nullability.NonNullable));
        AssertTagByte(13, arena.Map(i32, i32, keysSorted: false, Nullability.NonNullable));
    }

    /// <summary>
    /// The same pinning in the other direction: a hand-built buffer carrying literal tag <c>t</c>
    /// must decode to the kind the schema assigns to <c>t</c>. Without this the reader's tag
    /// table could be permuted in step with the writer's and nothing would notice.
    /// </summary>
    [Fact]
    public void EveryLiteralTag_DecodesToItsSchemaKind()
    {
        DTypeKind[] expected =
        [
            DTypeKind.Null, DTypeKind.Bool, DTypeKind.Primitive, DTypeKind.Decimal, DTypeKind.Utf8,
            DTypeKind.Binary, DTypeKind.Struct, DTypeKind.List, DTypeKind.Extension,
            DTypeKind.FixedSizeList, DTypeKind.Variant, DTypeKind.Union, DTypeKind.Map,
        ];

        for (byte tag = 1; tag <= 13; tag++)
        {
            using FlatBufferBuilder builder = new FlatBufferBuilder();
            int value = BuildValueTableForTag(builder, tag);
            byte[] buffer = BuildRoot(builder, tag, value);
            DType decoded = DTypeFlatBuffers.Read(buffer, new DTypeArena());
            Assert.Equal(expected[tag - 1], decoded.Kind);
        }
    }

    /// <summary>
    /// Pins the field id of every field of every case table, by decoding buffers whose fields are
    /// placed at the literal slots the schema declares.
    /// </summary>
    /// <remarks>
    /// The same blind spot as <see cref="EveryKind_EmitsItsSchemaTagByte"/>, one level down: swap
    /// <c>Struct_.names</c> and <c>Struct_.dtypes</c> in both the reader and the writer and every
    /// round trip — and the Protobuf equivalence property, which never sees a FlatBuffers slot —
    /// still passes. The payloads below are deliberately asymmetric so that any transposition
    /// changes the decoded value rather than merely relocating it.
    /// </remarks>
    [Fact]
    public void EveryCaseTable_DecodesItsFieldsAtTheirSchemaSlots()
    {
        // Bool / Utf8 / Binary / Variant: `nullable: bool` is field 0.
        foreach (byte tag in new byte[] { 2, 5, 6, 11 })
        {
            using FlatBufferBuilder leaf = new FlatBufferBuilder();
            leaf.StartTable();
            leaf.AddBool(0, true);
            Assert.True(DTypeFlatBuffers.Read(BuildRoot(leaf, tag, leaf.EndTable()), new DTypeArena()).IsNullable);
        }

        // table Primitive { ptype: PType; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            b.StartTable();
            b.AddUInt8(0, (byte)PType.I64);
            b.AddBool(1, true);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 3, b.EndTable()), new DTypeArena());
            Assert.Equal(PType.I64, d.PType);
            Assert.True(d.IsNullable);
        }

        // table Decimal { precision: uint8; scale: int8; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            b.StartTable();
            b.AddUInt8(0, 20);
            b.AddInt8(1, -3);
            b.AddBool(2, true);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 4, b.EndTable()), new DTypeArena());
            Assert.Equal(20, d.Precision);
            Assert.Equal(-3, d.Scale);
            Assert.True(d.IsNullable);
        }

        // table Struct_ { names: [string]; dtypes: [DType]; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            int names = b.CreateOffsetVector([b.CreateString("only")]);
            int dtypes = b.CreateOffsetVector([WriteI32DType(b)]);
            b.StartTable();
            b.AddOffset(0, names);
            b.AddOffset(1, dtypes);
            b.AddBool(2, true);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 7, b.EndTable()), new DTypeArena());
            Assert.Equal(1, d.FieldCount);
            Assert.Equal("only", d.GetFieldName(0));
            Assert.Equal(PType.I32, d.GetField(0).PType);
            Assert.True(d.IsNullable);
        }

        // table List { element_type: DType; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            int element = WriteI32DType(b);
            b.StartTable();
            b.AddOffset(0, element);
            b.AddBool(1, true);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 8, b.EndTable()), new DTypeArena());
            Assert.Equal(PType.I32, d.ElementType.PType);
            Assert.True(d.IsNullable);
        }

        // table Extension { id: string; storage_dtype: DType; metadata: [ubyte]; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            int id = b.CreateString("vortex.date");
            int storage = WriteI32DType(b);
            int metadata = b.CreateByteVector([0xAB, 0xCD]);
            b.StartTable();
            b.AddOffset(0, id);
            b.AddOffset(1, storage);
            b.AddOffset(2, metadata);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 9, b.EndTable()), new DTypeArena());
            Assert.Equal("vortex.date", d.ExtensionId);
            Assert.Equal(PType.I32, d.StorageType.PType);
            Assert.Equal(new byte[] { 0xAB, 0xCD }, d.ExtensionMetadata.ToArray());
        }

        // table FixedSizeList { element_type: DType; size: uint32; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            int element = WriteI32DType(b);
            b.StartTable();
            b.AddOffset(0, element);
            b.AddUInt32(1, 5);
            b.AddBool(2, true);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 10, b.EndTable()), new DTypeArena());
            Assert.Equal(PType.I32, d.ElementType.PType);
            Assert.Equal(5u, d.FixedSize);
            Assert.True(d.IsNullable);
        }

        // table Union { names: [string]; dtypes: [DType]; type_ids: [byte]; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            int names = b.CreateOffsetVector([b.CreateString("alt")]);
            int dtypes = b.CreateOffsetVector([WriteI32DType(b)]);
            int typeIds = b.CreateByteVector([200]);
            b.StartTable();
            b.AddOffset(0, names);
            b.AddOffset(1, dtypes);
            b.AddOffset(2, typeIds);
            b.AddBool(3, true);
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 12, b.EndTable()), new DTypeArena());
            Assert.Equal(1, d.FieldCount);
            Assert.Equal("alt", d.GetFieldName(0));
            Assert.Equal(200, d.GetTypeId(0));
            Assert.True(d.IsNullable);
        }

        // table Map { key_type: DType; value_type: DType; keys_sorted: bool; nullable: bool; }
        using (FlatBufferBuilder b = new FlatBufferBuilder())
        {
            int key = WriteUtf8DType(b);
            int value = WriteI32DType(b);
            b.StartTable();
            b.AddOffset(0, key);
            b.AddOffset(1, value);
            b.AddBool(2, true);        // keys_sorted
            b.AddBool(3, false);       // nullable -- deliberately the opposite of keys_sorted
            DType d = DTypeFlatBuffers.Read(BuildRoot(b, 13, b.EndTable()), new DTypeArena());
            Assert.Equal(DTypeKind.Utf8, d.KeyType.Kind);
            Assert.Equal(PType.I32, d.ValueType.PType);
            Assert.True(d.KeysSorted);
            Assert.False(d.IsNullable);
        }
    }

    /// <summary>The 13 model kinds and the 13 union tags are the same numbers.</summary>
    [Fact]
    public void DTypeKindValues_MatchTheUnionTags()
    {
        Assert.Equal(1, (byte)DTypeKind.Null);
        Assert.Equal(2, (byte)DTypeKind.Bool);
        Assert.Equal(3, (byte)DTypeKind.Primitive);
        Assert.Equal(4, (byte)DTypeKind.Decimal);
        Assert.Equal(5, (byte)DTypeKind.Utf8);
        Assert.Equal(6, (byte)DTypeKind.Binary);
        Assert.Equal(7, (byte)DTypeKind.Struct);
        Assert.Equal(8, (byte)DTypeKind.List);
        Assert.Equal(9, (byte)DTypeKind.Extension);
        Assert.Equal(10, (byte)DTypeKind.FixedSizeList);
        Assert.Equal(11, (byte)DTypeKind.Variant);
        Assert.Equal(12, (byte)DTypeKind.Union);
        Assert.Equal(13, (byte)DTypeKind.Map);
    }

    // ------------------------------------------------------------------ malformed roots

    [Fact]
    public void EmptyBuffer_IsRejected() => AssertRejected([]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BufferShorterThanARootUOffset_IsRejected(int length) => AssertRejected(new byte[length]);

    [Fact]
    public void ZeroRootUOffset_IsRejected()
    {
        // A uoffset of 0 is never a valid forward reference.
        AssertRejected(new byte[16]);
    }

    [Fact]
    public void RootUOffsetPastEnd_IsRejected()
    {
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 13);   // 13 + 4 > 16
        AssertRejected(buffer);
    }

    [Fact]
    public void RootUOffsetNearUIntMax_IsRejectedRatherThanWrapped()
    {
        // The offset arithmetic must be done in 64 bits: 0xFFFFFFFF + a small position wraps to a
        // small positive int if it is done in 32.
        foreach (uint offset in new uint[] { uint.MaxValue, uint.MaxValue - 3, 0x8000_0000u })
        {
            byte[] buffer = new byte[64];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, offset);
            AssertRejected(buffer);
        }
    }

    // ------------------------------------------------------------------ malformed unions

    [Fact]
    public void AbsentTypeTag_ReadsAsNone_AndIsRejected()
    {
        // AddUInt8 omits a value equal to its default, so writing tag 0 leaves the slot absent --
        // which is exactly how a reader sees a union whose discriminant was never written.
        byte[] buffer = BuildRootWithTag(0, forceDefaults: false);
        VortexFormatException ex = AssertRejected(buffer);
        Assert.Contains("NONE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitZeroTypeTag_IsRejected()
    {
        byte[] buffer = BuildRootWithTag(0, forceDefaults: true);
        Assert.Contains("NONE", AssertRejected(buffer).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public void UndefinedTypeTag_IsRejected(byte tag) => AssertRejected(BuildRootWithTag(tag, forceDefaults: false));

    [Fact]
    public void TagWithoutValueTable_IsRejected()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(0, 2);          // Bool, but no value table
        int root = builder.EndTable();
        AssertRejected(builder.FinishToArray(root));
    }

    [Fact]
    public void NullSubTable_IsRejectedByReadTable()
    {
        FlatBufferTable absent = default;
        Assert.True(absent.IsNull);
        DTypeArena arena = new DTypeArena();
        try
        {
            DTypeFlatBuffers.ReadTable(in absent, arena);
            Assert.Fail("An absent DType table must be rejected.");
        }
        catch (VortexFormatException)
        {
        }
    }

    // ------------------------------------------------------------------ malformed payloads

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(200)]
    [InlineData(255)]
    public void UndefinedPType_IsRejected(byte ptype)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(0, ptype);
        int value = builder.EndTable();
        AssertRejected(BuildRoot(builder, 3, value));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    public void StructWithMismatchedParallelVectors_IsRejected(int nameCount, int dtypeCount)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int names = CreateStringVector(builder, nameCount);
        int dtypes = CreateI32DTypeVector(builder, dtypeCount);

        builder.StartTable();
        builder.AddOffset(0, names);
        builder.AddOffset(1, dtypes);
        int value = builder.EndTable();

        AssertRejected(BuildRoot(builder, 7, value));
    }

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(2, 2, 3)]
    [InlineData(1, 2, 2)]
    public void UnionWithMismatchedParallelVectors_IsRejected(int nameCount, int dtypeCount, int typeIdCount)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int names = CreateStringVector(builder, nameCount);
        int dtypes = CreateI32DTypeVector(builder, dtypeCount);
        int typeIds = builder.CreateByteVector(new byte[typeIdCount]);

        builder.StartTable();
        builder.AddOffset(0, names);
        builder.AddOffset(1, dtypes);
        builder.AddOffset(2, typeIds);
        int value = builder.EndTable();

        AssertRejected(BuildRoot(builder, 12, value));
    }

    [Theory]
    [InlineData(8)]     // List.element_type
    [InlineData(10)]    // FixedSizeList.element_type
    public void ListWithoutElementType_IsRejected(byte tag)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddBool(tag == 8 ? 1 : 2, true);
        int value = builder.EndTable();
        AssertRejected(BuildRoot(builder, tag, value));
    }

    [Fact]
    public void ExtensionWithoutStorageDType_IsRejected()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int id = builder.CreateString("vortex.date");
        builder.StartTable();
        builder.AddOffset(0, id);
        int value = builder.EndTable();
        AssertRejected(BuildRoot(builder, 9, value));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MapWithoutBothChildren_IsRejected(bool hasKey, bool hasValue)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int key = hasKey ? WriteI32DType(builder) : 0;
        int val = hasValue ? WriteI32DType(builder) : 0;

        builder.StartTable();
        builder.AddOffset(0, key);
        builder.AddOffset(1, val);
        int value = builder.EndTable();
        AssertRejected(BuildRoot(builder, 13, value));
    }

    [Theory]
    [InlineData(0)]                  // precision 0 cannot represent a digit
    [InlineData(77)]                 // above MAX_PRECISION, which is i256's
    [InlineData(200)]
    [InlineData(255)]
    public void DecimalPrecisionOutOfRange_IsRejected(byte precision)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(0, precision);
        builder.AddInt8(1, 2);
        int value = builder.EndTable();
        AssertRejected(BuildRoot(builder, 4, value));
    }

    [Fact]
    public void DecimalPositiveScaleAbovePrecision_IsRejected()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(0, 4);
        builder.AddInt8(1, 5);
        int value = builder.EndTable();
        AssertRejected(BuildRoot(builder, 4, value));
    }

    // ------------------------------------------------------------------ depth

    [Fact]
    public void NestingBeyondTheDTypeDepthCap_IsRejected()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder(8192);
        int inner = WriteI32DType(builder);
        for (int i = 0; i < VortexLimits.MaxDTypeDepth + 40; i++)
        {
            inner = WrapInList(builder, inner);
        }

        byte[] buffer = builder.FinishToArray(inner);
        VortexFormatException ex = AssertRejected(buffer);
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cap itself must be reachable. A 64-deep chain costs 127 FlatBuffers table levels (each
    /// dtype level is a DType table plus its union value table), which has to stay inside
    /// <see cref="VortexLimits.MaxFlatBufferDepth"/> or the two caps would contradict each other.
    /// </summary>
    [Fact]
    public void NestingExactlyAtTheDepthCap_RoundTrips()
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.Primitive(PType.I32, Nullability.NonNullable);
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            d = arena.List(d, Nullability.Nullable);
        }

        Assert.Equal(d, RoundTrip(d));
    }

    // ------------------------------------------------------------------ shared children (DAG)

    /// <summary>
    /// Forward-only uoffsets make cycles impossible but they do NOT make sharing impossible: two
    /// slots may legally resolve to the same child table. Every level below points BOTH of its
    /// <c>dtypes</c> entries at the same child, so 3 KB of bytes describe a DAG with 2^63
    /// root-to-leaf paths. A reader that recurses once per edge never returns; a reader that
    /// decodes each position once returns 64 nodes.
    /// </summary>
    [Fact]
    public void StructWithSharedChildTables_IsDecodedOncePerPositionNotOncePerPath()
    {
        byte[] buffer = SharedStructChain(VortexLimits.MaxDTypeDepth - 1);
        DTypeArena arena = new DTypeArena();
        DType d = DTypeFlatBuffers.Read(buffer, arena);

        Assert.Equal(DTypeKind.Struct, d.Kind);
        Assert.Equal(2, d.FieldCount);
        Assert.Equal(d.GetField(0), d.GetField(1));

        // The arena dedups, so a correct decode is one node per level plus the i32 leaf.
        Assert.Equal(VortexLimits.MaxDTypeDepth, arena.NodeCount);
    }

    /// <summary>
    /// The engine walks a shared dtype once per node, but its public form spells out every path.
    /// One that spells out to more types than a FlatBuffer may hold tables is refused rather than
    /// expanded; a shared dtype that spells out to less converts as before.
    /// </summary>
    [Fact]
    public void ASharedDTypeIsRefusedAsAPublicTypeRatherThanSpelledOut()
    {
        VortexType small = VortexTypes.FromDType(DTypeFlatBuffers.Read(SharedStructChain(10), new DTypeArena()));
        Assert.Equal(VortexTypeKind.Struct, small.Kind);
        Assert.Equal(2, small.Fields.Length);

        DType wide = DTypeFlatBuffers.Read(SharedStructChain(21), new DTypeArena());
        Assert.Throws<VortexFormatException>(() => VortexTypes.FromDType(wide));

        DType deepest = DTypeFlatBuffers.Read(SharedStructChain(VortexLimits.MaxDTypeDepth - 1), new DTypeArena());
        Assert.Throws<VortexFormatException>(() => VortexTypes.SchemaOf(deepest));
    }

    /// <summary>
    /// Every field name of a struct may point at one string. The names still to intern size the
    /// arena's growth, which must not count that string once per field: here 4 096 references to
    /// 16 KiB would reserve 64 MiB for a 48 KiB buffer.
    /// </summary>
    [Fact]
    public void FieldNamesSharingOneStringReserveNoMoreThanTheBufferHolds()
    {
        byte[] buffer = StructOfOneSharedName(fields: 4_096, nameLength: 16 << 10);
        DTypeArena arena = new DTypeArena();
        long before = GC.GetAllocatedBytesForCurrentThread();
        DType d = DTypeFlatBuffers.Read(buffer, arena);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(4_096, d.FieldCount);
        Assert.True(allocated < 8L * buffer.Length, $"{allocated} bytes allocated to read a {buffer.Length}-byte dtype");
    }

    /// <summary>
    /// The public form spells a shared name out once per field: 8 192 references to 16 KiB would
    /// be 256 MiB of strings from a 80 KiB buffer, so the conversion refuses it.
    /// </summary>
    [Fact]
    public void FieldNamesSharingOneStringAreNotSpelledOutWithoutBound()
    {
        DType d = DTypeFlatBuffers.Read(StructOfOneSharedName(fields: 8_192, nameLength: 16 << 10), new DTypeArena());
        Assert.Throws<VortexFormatException>(() => VortexTypes.FromDType(d));
    }

    /// <summary>
    /// The same defect through the two-child case the arithmetic is cheapest in: a
    /// <c>Map</c> whose <c>key_type</c> and <c>value_type</c> are one shared table.
    /// </summary>
    [Fact]
    public void MapWithSharedKeyAndValueTables_IsDecodedOncePerPosition()
    {
        const int Levels = VortexLimits.MaxDTypeDepth - 1;

        using FlatBufferBuilder builder = new FlatBufferBuilder(8192);
        int child = WriteI32DType(builder);
        for (int i = 0; i < Levels; i++)
        {
            builder.StartTable();
            builder.AddOffset(0, child);    // key_type
            builder.AddOffset(1, child);    // value_type, the same table
            int value = builder.EndTable();

            builder.StartTable();
            builder.AddUInt8(0, 13);
            builder.AddOffset(1, value);
            child = builder.EndTable();
        }

        byte[] buffer = builder.FinishToArray(child);
        DTypeArena arena = new DTypeArena();
        DType d = DTypeFlatBuffers.Read(buffer, arena);

        Assert.Equal(DTypeKind.Map, d.Kind);
        Assert.Equal(d.KeyType, d.ValueType);
        Assert.Equal(VortexLimits.MaxDTypeDepth, arena.NodeCount);
    }

    /// <summary>
    /// The soundness condition for decoding a shared position once: sharing lets the SAME table be
    /// reached at two different depths, so a subtree that fits when reached shallow may not fit
    /// when reached deep. Here a 30-level shared Map chain (enough sharing that the walk starts
    /// memoising) is referenced both directly and through a 40-level list chain: 40 + 31 = 71
    /// levels. Returning the shallow decode for the deep reference must not turn an illegal dtype
    /// into a legal one — the arena recomputes each node's depth from its children's and rejects
    /// at exactly the same point the recursion would have.
    /// </summary>
    [Fact]
    public void ASharedSubtreeReReachedPastTheDepthCapIsStillRejected()
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder(16384);
        int shared = WriteI32DType(builder);
        for (int i = 0; i < 30; i++)
        {
            builder.StartTable();
            builder.AddOffset(0, shared);
            builder.AddOffset(1, shared);
            int map = builder.EndTable();

            builder.StartTable();
            builder.AddUInt8(0, 13);
            builder.AddOffset(1, map);
            shared = builder.EndTable();
        }

        int deep = shared;
        for (int i = 0; i < 40; i++)
        {
            deep = WrapInList(builder, deep);
        }

        int shallowName = builder.CreateString("shallow");
        int deepName = builder.CreateString("deep");
        int names = builder.CreateOffsetVector([shallowName, deepName]);
        int dtypes = builder.CreateOffsetVector([shared, deep]);

        builder.StartTable();
        builder.AddOffset(0, names);
        builder.AddOffset(1, dtypes);
        int value = builder.EndTable();

        builder.StartTable();
        builder.AddUInt8(0, 7);
        builder.AddOffset(1, value);
        byte[] buffer = builder.FinishToArray(builder.EndTable());

        VortexFormatException ex = AssertRejected(buffer);
        Assert.Contains("DType nesting depth 65", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The FlatBuffers layer's own backstop: a traversal may carry a total-table budget, which
    /// bounds work even for a consumer that does not memoise (the reference verifiers'
    /// <c>max_tables</c>). Depth cannot substitute for it — a DAG stays inside every depth cap.
    /// </summary>
    [Fact]
    public void ATableBudgetBoundsTheTotalNumberOfTablesVisited()
    {
        byte[] buffer = DTypeFlatBuffers.Serialize(SampleNestedDType());

        // The root alone exhausts a budget of one, so the first child is refused.
        int tiny = 1;
        FlatBufferTable root = FlatBufferTable.Root(buffer, ref tiny);
        Assert.Equal(0, tiny);
        try
        {
            _ = root.GetTable(1).IsNull;
            Assert.Fail("A traversal past its table budget must be rejected.");
        }
        catch (VortexFormatException)
        {
        }

        // And it bounds a whole dtype read, not merely one accessor.
        int small = 4;
        FlatBufferTable bounded = FlatBufferTable.Root(buffer, ref small);
        try
        {
            DTypeFlatBuffers.ReadTable(in bounded, new DTypeArena());
            Assert.Fail("A dtype read past its table budget must be rejected.");
        }
        catch (VortexFormatException)
        {
        }

        // A real budget is never in the way: the same buffer reads with plenty to spare.
        int full = VortexLimits.MaxFlatBufferTables;
        FlatBufferTable ample = FlatBufferTable.Root(buffer, ref full);
        Assert.Equal(SampleNestedDType(), DTypeFlatBuffers.ReadTable(in ample, new DTypeArena()));
        Assert.True(full > VortexLimits.MaxFlatBufferTables - 1000, $"budget left: {full}");
    }

    // ------------------------------------------------------------------ hostile mutation

    [Fact]
    public void EveryTruncation_ThrowsNothingButVortexFormatException()
    {
        byte[] full = DTypeFlatBuffers.Serialize(SampleNestedDType());
        for (int length = 0; length < full.Length; length++)
        {
            AssertOnlyFormatFailures(full[..length], $"prefix of length {length}");
        }
    }

    [Fact]
    public void EverySingleByteCorruption_ThrowsNothingButVortexFormatException()
    {
        byte[] full = DTypeFlatBuffers.Serialize(SampleNestedDType());
        foreach (byte mask in new byte[] { 0xFF, 0x01, 0x80 })
        {
            for (int i = 0; i < full.Length; i++)
            {
                byte[] corrupt = (byte[])full.Clone();
                corrupt[i] ^= mask;
                AssertOnlyFormatFailures(corrupt, $"byte {i} xor 0x{mask:X2}");
            }
        }
    }

    // ------------------------------------------------------------------ round trips

    [Fact]
    public void EveryPType_RoundTrips()
    {
        DTypeArena arena = new DTypeArena();
        for (byte tag = 0; tag <= PTypeExtensions.MaxPType; tag++)
        {
            foreach (Nullability n in new[] { Nullability.NonNullable, Nullability.Nullable })
            {
                DType d = arena.Primitive((PType)tag, n);
                DType back = RoundTrip(d);
                Assert.Equal(d, back);
                Assert.Equal((PType)tag, back.PType);
                Assert.Equal(n, back.Nullability);
            }
        }
    }

    [Fact]
    public void NullDType_RoundTripsThroughAnEmptyValueTable()
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.Null(Nullability.Nullable);
        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal(DTypeKind.Null, back.Kind);
        Assert.True(back.IsNullable);
    }

    [Fact]
    public void EmptyStruct_RoundTrips()
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.Nullable);
        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal(0, back.FieldCount);
    }

    [Fact]
    public void EmptyUnion_RoundTrips()
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.Union(
            ReadOnlySpan<int>.Empty, ReadOnlySpan<DType>.Empty, ReadOnlySpan<byte>.Empty, Nullability.NonNullable);
        Assert.Equal(d, RoundTrip(d));
    }

    [Fact]
    public void StructWithDuplicateFieldNamesAndAnEmptyName_RoundTrips()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        DType d = arena.Struct(["a", "a", "", ""], [i32, utf8, i32, utf8], Nullability.NonNullable);

        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal(4, back.FieldCount);
        Assert.Equal("a", back.GetFieldName(0));
        Assert.Equal("a", back.GetFieldName(1));
        Assert.Equal(string.Empty, back.GetFieldName(2));
        Assert.Equal(utf8, back.GetField(3));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void FixedSizeListSizeBoundaries_RoundTrip(uint size)
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.FixedSizeList(arena.Primitive(PType.F32, Nullability.NonNullable), size, Nullability.Nullable);
        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal(size, back.FixedSize);
    }

    [Fact]
    public void UnionTypeIdsAreUnsigned()
    {
        // The schema's `type_ids: [byte]` is to be interpreted as unsigned: 255 must come back
        // as 255, not as -1 sign-extended into something else.
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType d = arena.Union(
            [arena.InternName("lo"), arena.InternName("mid"), arena.InternName("hi")],
            [i32, i32, i32],
            [0, 128, 255],
            Nullability.Nullable);

        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal(0, back.GetTypeId(0));
        Assert.Equal(128, back.GetTypeId(1));
        Assert.Equal(255, back.GetTypeId(2));
    }

    [Fact]
    public void ExtensionMetadata_AbsentAndEmptyBothReadAsEmpty()
    {
        DTypeArena arena = new DTypeArena();
        DType storage = arena.Primitive(PType.I64, Nullability.Nullable);
        DType withNone = arena.Extension("vortex.timestamp", storage, ReadOnlySpan<byte>.Empty);
        DType withBytes = arena.Extension("vortex.timestamp", storage, [1, 2, 3]);

        DType backNone = RoundTrip(withNone);
        Assert.Equal(withNone, backNone);
        Assert.True(backNone.ExtensionMetadata.IsEmpty);

        DType backBytes = RoundTrip(withBytes);
        Assert.Equal(withBytes, backBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, backBytes.ExtensionMetadata.ToArray());

        // Our READER accepts both encodings: absent and present-but-empty read the same.
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int offset = DTypeFlatBuffers.Write(builder, withNone);
        byte[] buffer = builder.FinishToArray(offset);
        Assert.Equal(withNone, DTypeFlatBuffers.Read(buffer, new DTypeArena()));
    }

    [Fact]
    public void ExtensionMetadata_IsWrittenPresentEvenWhenEmpty()
    {
        // OUR WRITER MUST NOT USE THE SHORTER ENCODING. The reference requires the field: its
        // FlatBuffers dtype reader does
        // `fb_ext.metadata().ok_or_else(|| vortex_err!("failed to parse extension metadata ..."))`,
        // so an omitted vector makes the dtype unreadable by Vortex Rust.
        //
        // A round trip cannot see this: our reader collapses absent and empty, so it passes while
        // the reference rejects the file. Only the Rust cross-check and this probe catch it.
        DTypeArena arena = new DTypeArena();
        DType storage = arena.FixedSizeList(
            arena.Primitive(PType.U8, Nullability.NonNullable), 16, Nullability.NonNullable);
        DType uuid = arena.Extension("vortex.uuid", storage, ReadOnlySpan<byte>.Empty);

        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int offset = DTypeFlatBuffers.Write(builder, uuid);
        byte[] buffer = builder.FinishToArray(offset);

        // Read the extension table's own vtable rather than the model, which cannot tell the two
        // encodings apart: slot 2 must resolve to a vector, not to nothing. The root DType table is
        // a union, so its value sits in slot 1 and the tag in slot 0.
        FlatBufferTable root = FlatBufferTable.Root(buffer);
        FlatBufferTable extension = root.GetTable(1);
        Assert.True(
            extension.HasField(2),
            "the extension's metadata vector must be present, even at zero length");
        Assert.True(extension.GetByteVector(2).IsEmpty);
    }

    [Fact]
    public void ExtensionNullabilityFollowsItsStorageDType()
    {
        DTypeArena arena = new DTypeArena();
        DType nonNull = arena.Extension("x", arena.Primitive(PType.I32, Nullability.NonNullable), []);
        DType nullable = arena.Extension("x", arena.Primitive(PType.I32, Nullability.Nullable), []);

        Assert.False(RoundTrip(nonNull).IsNullable);
        Assert.True(RoundTrip(nullable).IsNullable);
        Assert.NotEqual(nonNull, nullable);
    }

    [Fact]
    public void DecimalBoundaries_RoundTrip()
    {
        DTypeArena arena = new DTypeArena();
        (byte Precision, sbyte Scale)[] cases =
        [
            (1, 0), (1, 1), (1, -1), (18, 18), (38, -38), (76, 76), (76, 0), (76, -128), (2, -128),
        ];

        foreach ((byte precision, sbyte scale) in cases)
        {
            DType d = arena.Decimal(precision, scale, Nullability.Nullable);
            DType back = RoundTrip(d);
            Assert.Equal(d, back);
            Assert.Equal(precision, back.Precision);
            Assert.Equal(scale, back.Scale);
        }
    }

    [Fact]
    public void WideStruct_RoundTripsWithSharedVTables()
    {
        // Vtable dedup is mandatory, or a wide schema's metadata inflates, and it is the case
        // most likely to break a reader: 300 identical child tables all soffset to one vtable,
        // and the soffset is negative for every reused one.
        const int Fields = 300;
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        string[] names = new string[Fields];
        DType[] fields = new DType[Fields];
        for (int i = 0; i < Fields; i++)
        {
            names[i] = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[i] = i32;
        }

        DType d = arena.Struct(names, fields, Nullability.NonNullable);
        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal(Fields, back.FieldCount);
        Assert.Equal("c299", back.GetFieldName(299));
    }

    [Fact]
    public void NonAsciiFieldNamesAndExtensionIds_RoundTrip()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType inner = arena.Struct(["naïve", "日本語", "\U0001F600"], [i32, i32, i32], Nullability.Nullable);
        DType d = arena.Extension("vortex.été", inner, [0xFF, 0x00, 0x7F]);

        DType back = RoundTrip(d);
        Assert.Equal(d, back);
        Assert.Equal("vortex.été", back.ExtensionId);
        Assert.Equal("日本語", back.StorageType.GetFieldName(1));
    }

    [Fact]
    public void ExtensionWrappingAnExtension_RoundTrips()
    {
        DTypeArena arena = new DTypeArena();
        DType inner = arena.Extension("vortex.date", arena.Primitive(PType.I32, Nullability.Nullable), [7]);
        DType outer = arena.Extension("app.wrapped", inner, []);

        DType back = RoundTrip(outer);
        Assert.Equal(outer, back);
        Assert.Equal(DTypeKind.Extension, back.StorageType.Kind);
        Assert.Equal("vortex.date", back.StorageType.ExtensionId);
    }

    // ------------------------------------------------------------------ embedded use

    [Fact]
    public void ReadTable_ReadsADTypeEmbeddedInAnotherFlatBuffer()
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.Map(
            arena.Utf8(Nullability.NonNullable),
            arena.List(arena.Primitive(PType.F64, Nullability.Nullable), Nullability.Nullable),
            keysSorted: true,
            Nullability.NonNullable);

        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int dtypeOffset = DTypeFlatBuffers.Write(builder, d);
        builder.StartTable();
        builder.AddInt32(0, 4242);
        builder.AddOffset(1, dtypeOffset);
        int outer = builder.EndTable();
        byte[] buffer = builder.FinishToArray(outer);

        FlatBufferTable root = FlatBufferTable.Root(buffer);
        Assert.Equal(4242, root.GetInt32(0));
        FlatBufferTable embedded = root.GetTable(1);
        Assert.Equal(d, DTypeFlatBuffers.ReadTable(in embedded, new DTypeArena()));
    }

    [Fact]
    public void ReadTable_HonoursTheDepthArgument()
    {
        DTypeArena arena = new DTypeArena();
        DType d = arena.Primitive(PType.I32, Nullability.NonNullable);
        byte[] buffer = DTypeFlatBuffers.Serialize(d);
        FlatBufferTable root = FlatBufferTable.Root(buffer);

        // A caller that has already descended to the cap must not be able to add one more level.
        Assert.Equal(d, DTypeFlatBuffers.ReadTable(in root, new DTypeArena(), VortexLimits.MaxDTypeDepth - 1));
        try
        {
            DTypeFlatBuffers.ReadTable(in root, new DTypeArena(), VortexLimits.MaxDTypeDepth);
            Assert.Fail("Reading at the depth cap must be rejected.");
        }
        catch (VortexFormatException)
        {
        }
    }

    // ------------------------------------------------------------------ argument contracts

    [Fact]
    public void NullArena_IsAnArgumentNullException()
    {
        byte[] buffer = DTypeFlatBuffers.Serialize(new DTypeArena().Bool(Nullability.NonNullable));
        Assert.Throws<ArgumentNullException>(() => DTypeFlatBuffers.Read(buffer, null!));
    }

    [Fact]
    public void NullBuilder_IsAnArgumentNullException()
    {
        DType d = new DTypeArena().Bool(Nullability.NonNullable);
        Assert.Throws<ArgumentNullException>(() => DTypeFlatBuffers.Write(null!, d));
    }

    [Fact]
    public void DefaultDType_CannotBeWritten()
    {
        Assert.Throws<ArgumentException>(() => DTypeFlatBuffers.Serialize(default));

        using FlatBufferBuilder builder = new FlatBufferBuilder();
        Assert.Throws<ArgumentException>(() => DTypeFlatBuffers.Write(builder, default));
    }

    // ------------------------------------------------------------------ helpers

    private static DType SampleNestedDType()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        DType ext = arena.Extension("vortex.date", i32, [9, 8, 7]);
        DType inner = arena.Struct(["id", "name"], [i32, utf8], Nullability.NonNullable);
        DType list = arena.List(inner, Nullability.Nullable);
        DType map = arena.Map(utf8, list, keysSorted: false, Nullability.Nullable);
        return arena.Struct(["a", "b", "c"], [ext, map, arena.Decimal(19, -3, Nullability.Nullable)],
            Nullability.Nullable);
    }

    private static DType RoundTrip(DType dtype) =>
        DTypeFlatBuffers.Read(DTypeFlatBuffers.Serialize(dtype), new DTypeArena());

    private static VortexFormatException AssertRejected(byte[] buffer)
    {
        DTypeArena arena = new DTypeArena();
        return Assert.Throws<VortexFormatException>(() => DTypeFlatBuffers.Read(buffer, arena));
    }

    private static void AssertOnlyFormatFailures(byte[] buffer, string what)
    {
        DTypeArena arena = new DTypeArena();
        try
        {
            DTypeFlatBuffers.Read(buffer, arena);
        }
        catch (VortexFormatException)
        {
            // The one exception type a hostile buffer is allowed to produce.
        }
        catch (Exception ex)
        {
            Assert.Fail($"{what} threw {ex.GetType().FullName} instead of VortexFormatException: {ex.Message}");
        }
    }

    /// <summary>Builds a root <c>DType</c> table with an arbitrary tag and no value table.</summary>
    private static byte[] BuildRootWithTag(byte tag, bool forceDefaults)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        builder.StartTable();
        int value = builder.EndTable();          // an empty Null-shaped value table

        builder.ForceDefaults = forceDefaults;
        builder.StartTable();
        builder.AddUInt8(0, tag);
        builder.AddOffset(1, value);
        int root = builder.EndTable();
        return builder.FinishToArray(root);
    }

    /// <summary>Wraps an already-written value table in a root <c>DType</c> table.</summary>
    private static byte[] BuildRoot(FlatBufferBuilder builder, byte tag, int valueOffset)
    {
        builder.StartTable();
        builder.AddUInt8(0, tag);
        builder.AddOffset(1, valueOffset);
        int root = builder.EndTable();
        return builder.FinishToArray(root);
    }

    private static int WriteI32DType(FlatBufferBuilder builder)
    {
        builder.StartTable();
        builder.AddUInt8(0, (byte)PType.I32);
        int value = builder.EndTable();

        builder.StartTable();
        builder.AddUInt8(0, 3);
        builder.AddOffset(1, value);
        return builder.EndTable();
    }

    private static int WriteUtf8DType(FlatBufferBuilder builder)
    {
        builder.StartTable();
        int value = builder.EndTable();

        builder.StartTable();
        builder.AddUInt8(0, 5);
        builder.AddOffset(1, value);
        return builder.EndTable();
    }

    /// <summary>
    /// <paramref name="levels"/> structs, each pointing both its fields at the same child, over an
    /// i32: 2^levels root-to-leaf paths in a few bytes per level.
    /// </summary>
    private static byte[] SharedStructChain(int levels)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder(16384);
        int child = WriteI32DType(builder);
        for (int i = 0; i < levels; i++)
        {
            int a = builder.CreateString("a");
            int b = builder.CreateString("b");
            int names = builder.CreateOffsetVector([a, b]);

            // The whole point: one child offset, referenced twice.
            int dtypes = builder.CreateOffsetVector([child, child]);

            builder.StartTable();
            builder.AddOffset(0, names);
            builder.AddOffset(1, dtypes);
            int value = builder.EndTable();

            builder.StartTable();
            builder.AddUInt8(0, 7);
            builder.AddOffset(1, value);
            child = builder.EndTable();
        }

        return builder.FinishToArray(child);
    }

    /// <summary>A struct of <paramref name="fields"/> i32 fields whose names are all one string.</summary>
    private static byte[] StructOfOneSharedName(int fields, int nameLength)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder(nameLength + (fields * 8) + 1024);
        byte[] text = new byte[nameLength];
        text.AsSpan().Fill((byte)'n');
        int name = builder.CreateStringUtf8(text);
        int leaf = WriteI32DType(builder);
        int[] names = new int[fields];
        int[] dtypes = new int[fields];
        names.AsSpan().Fill(name);
        dtypes.AsSpan().Fill(leaf);
        int nameVector = builder.CreateOffsetVector(names);
        int dtypeVector = builder.CreateOffsetVector(dtypes);

        builder.StartTable();
        builder.AddOffset(0, nameVector);
        builder.AddOffset(1, dtypeVector);
        int value = builder.EndTable();

        builder.StartTable();
        builder.AddUInt8(0, 7);
        builder.AddOffset(1, value);
        return builder.FinishToArray(builder.EndTable());
    }

    private static int WrapInList(FlatBufferBuilder builder, int elementDTypeOffset)
    {
        builder.StartTable();
        builder.AddOffset(0, elementDTypeOffset);
        int value = builder.EndTable();

        builder.StartTable();
        builder.AddUInt8(0, 8);
        builder.AddOffset(1, value);
        return builder.EndTable();
    }

    private static int CreateStringVector(FlatBufferBuilder builder, int count)
    {
        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = builder.CreateString("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.CreateOffsetVector(offsets);
    }

    private static int CreateI32DTypeVector(FlatBufferBuilder builder, int count)
    {
        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = WriteI32DType(builder);
        }

        return builder.CreateOffsetVector(offsets);
    }

    /// <summary>
    /// Serializes <paramref name="dtype"/> and pulls the union discriminant straight out of the
    /// root table's field-0 slot, with no help from <see cref="DTypeFlatBuffers"/>.
    /// </summary>
    private static void AssertTagByte(byte expected, DType dtype)
    {
        byte[] buffer = DTypeFlatBuffers.Serialize(dtype);
        int root = (int)ReadU32(buffer, 0);
        int vtable = root - ReadI32(buffer, root);
        int slot = ReadU16(buffer, vtable + 4);
        Assert.True(slot != 0, $"{dtype}: the union discriminant slot is absent.");
        Assert.Equal(expected, buffer[root + slot]);
    }

    /// <summary>
    /// Builds a value table shaped for <paramref name="tag"/>: only the children the case actually
    /// requires, so the resulting buffer is minimal but legal.
    /// </summary>
    private static int BuildValueTableForTag(FlatBufferBuilder builder, byte tag)
    {
        switch (tag)
        {
            case 4:     // Decimal: precision must be at least 1
                builder.StartTable();
                builder.AddUInt8(0, 5);
                builder.AddInt8(1, 2);
                return builder.EndTable();

            case 8:     // List.element_type
            case 10:    // FixedSizeList.element_type
            {
                int element = WriteI32DType(builder);
                builder.StartTable();
                builder.AddOffset(0, element);
                return builder.EndTable();
            }

            case 9:     // Extension.storage_dtype is field 1, not field 0
            {
                int storage = WriteI32DType(builder);
                builder.StartTable();
                builder.AddOffset(1, storage);
                return builder.EndTable();
            }

            case 13:    // Map.key_type and Map.value_type
            {
                int key = WriteI32DType(builder);
                int value = WriteI32DType(builder);
                builder.StartTable();
                builder.AddOffset(0, key);
                builder.AddOffset(1, value);
                return builder.EndTable();
            }

            default:
                // Null, Bool, Primitive, Utf8, Binary, Struct_, Variant and Union are all legal
                // with every field absent.
                builder.StartTable();
                return builder.EndTable();
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> buffer, int position) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position, sizeof(ushort)));

    private static uint ReadU32(ReadOnlySpan<byte> buffer, int position) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position, sizeof(uint)));

    private static int ReadI32(ReadOnlySpan<byte> buffer, int position) =>
        BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(position, sizeof(int)));
}
