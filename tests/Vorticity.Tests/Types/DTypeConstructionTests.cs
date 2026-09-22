using System;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// Construction-time validation. Everything here is reachable from a file, so the exception type
/// matters as much as the rejection: <see cref="VortexFormatException"/> for a value the wire can
/// carry, <see cref="ArgumentException"/> only for a caller mistake the wire cannot express.
/// </summary>
public sealed class DTypeConstructionTests
{
    // ------------------------------------------------------------------ decimal

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(2, 2)]
    [InlineData(18, 6)]
    [InlineData(19, -19)]
    [InlineData(38, 38)]
    [InlineData(38, -38)]
    // Precision above 38 selects i256 storage and is legal: MAX_PRECISION upstream is i256's.
    [InlineData(39, 0)]
    [InlineData(40, 2)]
    [InlineData(76, 76)]
    [InlineData(76, 0)]
    // A negative scale is bounded only by sbyte, NOT by -precision: upstream applies the
    // scale <= precision check under `if scale > 0`.
    [InlineData(1, -128)]
    [InlineData(10, -128)]
    [InlineData(38, -128)]
    [InlineData(76, -128)]
    public void DecimalAcceptsTheLegalRange(byte precision, int scale)
    {
        DTypeArena arena = new();
        DType d = arena.Decimal(precision, (sbyte)scale, Nullability.NonNullable);
        Assert.Equal(DTypeKind.Decimal, d.Kind);
        Assert.Equal(precision, d.Precision);
        Assert.Equal((sbyte)scale, d.Scale);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(77)]
    [InlineData(100)]
    [InlineData(255)]
    public void DecimalRejectsAPrecisionOutsideOneToSeventySix(byte precision)
    {
        // vortex-array/src/dtype/decimal/mod.rs: precision is NonZero and bounded by
        // MAX_PRECISION, which is i256's 76 -- not i128's 38.
        DTypeArena arena = new();
        Assert.Throws<VortexFormatException>(
            () => { arena.Decimal(precision, 0, Nullability.NonNullable); });
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(10, 11)]
    [InlineData(38, 39)]
    [InlineData(38, 127)]
    [InlineData(76, 77)]
    public void DecimalRejectsAPositiveScaleAboveThePrecision(byte precision, int scale)
    {
        DTypeArena arena = new();
        Assert.Throws<VortexFormatException>(
            () => { arena.Decimal(precision, (sbyte)scale, Nullability.NonNullable); });
    }

    // ------------------------------------------------------------------ ptype and nullability

    [Theory]
    [InlineData(11)]
    [InlineData(128)]
    [InlineData(255)]
    public void PrimitiveRejectsAnUndefinedPType(byte tag)
    {
        DTypeArena arena = new();
        Assert.Throws<VortexFormatException>(
            () => { arena.Primitive((PType)tag, Nullability.NonNullable); });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void NullabilityOutsideZeroAndOneIsRejected(byte value)
    {
        // The wire field is a bool, so an out-of-range enum can only come from a cast; accepting
        // it would create a node that no round trip could reproduce.
        DTypeArena arena = new();
        Assert.Throws<VortexFormatException>(
            () => { arena.Bool((Nullability)value); });
    }

    [Fact]
    public void WithNullabilityRejectsAnOutOfRangeValue()
    {
        DTypeArena arena = new();
        DType d = arena.Bool(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => { d.WithNullability((Nullability)9); });
    }

    // ------------------------------------------------------------------ arity mismatches

    [Fact]
    public void StructWithMoreNamesThanFieldsIsMalformed()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(
            () => { arena.Struct(["a", "b"], [i32], Nullability.NonNullable); });
    }

    [Fact]
    public void StructWithMoreFieldsThanNamesIsMalformed()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(
            () => { arena.Struct(["a"], [i32, i32], Nullability.NonNullable); });
    }

    [Fact]
    public void StructHandleOverloadAlsoChecksArity()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int a = arena.InternName("a"u8);
        int[] handles = [a, a];
        DType[] fields = [i32];
        Assert.Throws<VortexFormatException>(
            () => { arena.Struct(handles, fields, Nullability.NonNullable); });
    }

    [Fact]
    public void UnionTypeIdCountMustMatchFieldCount()
    {
        // The schema requires a union's type_ids to be exactly as long as its dtypes.
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int a = arena.InternName("a"u8);
        int[] handles = [a];
        DType[] fields = [i32];
        byte[] tooMany = [0, 1];
        Assert.Throws<VortexFormatException>(
            () => { arena.Union(handles, fields, tooMany, Nullability.NonNullable); });

        byte[] none = [];
        Assert.Throws<VortexFormatException>(
            () => { arena.Union(handles, fields, none, Nullability.NonNullable); });
    }

    [Fact]
    public void UnionNameCountMustMatchFieldCount()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int[] handles = [];
        DType[] fields = [i32];
        byte[] typeIds = [0];
        Assert.Throws<VortexFormatException>(
            () => { arena.Union(handles, fields, typeIds, Nullability.NonNullable); });
    }

    [Fact]
    public void EmptyStructAndEmptyUnionAreLegal()
    {
        DTypeArena arena = new();
        DType emptyStruct = arena.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.Nullable);
        DType emptyUnion = arena.Union(
            ReadOnlySpan<int>.Empty, ReadOnlySpan<DType>.Empty, ReadOnlySpan<byte>.Empty, Nullability.NonNullable);

        Assert.Equal(0, emptyStruct.FieldCount);
        Assert.Equal(0, emptyStruct.ChildCount);
        Assert.Equal(0, emptyUnion.FieldCount);
        Assert.NotEqual(emptyStruct, emptyUnion);
    }

    // ------------------------------------------------------------------ arena provenance

    [Fact]
    public void EveryCompositeRejectsAChildFromAnotherArena()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType mine = a.Primitive(PType.I32, Nullability.NonNullable);
        DType theirs = b.Primitive(PType.I32, Nullability.NonNullable);

        Assert.Throws<ArgumentException>(() => { a.List(theirs, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { a.FixedSizeList(theirs, 4, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { a.Map(theirs, mine, false, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { a.Map(mine, theirs, false, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { a.Extension("x", theirs, ReadOnlySpan<byte>.Empty); });

        DType[] mixed = [mine, theirs];
        Assert.Throws<ArgumentException>(() => { a.Struct(["p", "q"], mixed, Nullability.NonNullable); });

        int p = a.InternName("p"u8);
        int q = a.InternName("q"u8);
        int[] handles = [p, q];
        byte[] typeIds = [0, 1];
        Assert.Throws<ArgumentException>(() => { a.Union(handles, mixed, typeIds, Nullability.NonNullable); });
    }

    [Fact]
    public void EveryCompositeRejectsADefaultChild()
    {
        DTypeArena arena = new();
        DType none = default;
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);

        Assert.Throws<ArgumentException>(() => { arena.List(none, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { arena.FixedSizeList(none, 1, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { arena.Map(none, i32, false, Nullability.NonNullable); });
        Assert.Throws<ArgumentException>(() => { arena.Extension("x", none, ReadOnlySpan<byte>.Empty); });

        DType[] fields = [none];
        Assert.Throws<ArgumentException>(() => { arena.Struct(["a"], fields, Nullability.NonNullable); });
    }

    [Fact]
    public void AnExtensionWithoutAnIdIsMalformedWhenItBecomesASchema()
    {
        // The wire can carry an empty id, so the public schema built from it refuses the file,
        // not the caller.
        DTypeArena arena = new();
        DType anonymous = arena.Extension(ReadOnlySpan<byte>.Empty, arena.Primitive(PType.I64, Nullability.NonNullable), ReadOnlySpan<byte>.Empty);
        Assert.Throws<VortexFormatException>(() => VortexTypes.FromDType(anonymous));
    }

    // ------------------------------------------------------------------ depth cap

    [Fact]
    public void ListNestingIsAllowedUpToTheCap()
    {
        // Depth is capped because a 10 000-deep nested type blows the stack during schema
        // parsing, before any data is read. The leaf counts as depth 1.
        DTypeArena arena = new();
        DType d = arena.Primitive(PType.I32, Nullability.NonNullable);
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            d = arena.List(d, Nullability.NonNullable);
        }

        DType atCap = d;
        Assert.Throws<VortexFormatException>(() => { arena.List(atCap, Nullability.NonNullable); });
    }

    [Fact]
    public void StructNestingIsCappedToo()
    {
        DTypeArena arena = new();
        DType d = arena.Bool(Nullability.NonNullable);
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            d = arena.Struct(["f"], [d], Nullability.NonNullable);
        }

        DType atCap = d;
        DType[] fields = [atCap];
        Assert.Throws<VortexFormatException>(() => { arena.Struct(["f"], fields, Nullability.NonNullable); });
    }

    [Fact]
    public void EveryCompositeEnforcesTheDepthCap()
    {
        DTypeArena arena = new();
        DType atCap = arena.Bool(Nullability.NonNullable);
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            atCap = arena.List(atCap, Nullability.NonNullable);
        }

        DType deep = atCap;
        DType shallow = arena.Bool(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => { arena.FixedSizeList(deep, 2, Nullability.NonNullable); });
        Assert.Throws<VortexFormatException>(() => { arena.Map(deep, shallow, false, Nullability.NonNullable); });
        Assert.Throws<VortexFormatException>(() => { arena.Map(shallow, deep, false, Nullability.NonNullable); });
        Assert.Throws<VortexFormatException>(() => { arena.Extension("e", deep, ReadOnlySpan<byte>.Empty); });

        DType[] fields = [deep];
        int f = arena.InternName("f"u8);
        int[] handles = [f];
        byte[] typeIds = [0];
        Assert.Throws<VortexFormatException>(() => { arena.Union(handles, fields, typeIds, Nullability.NonNullable); });
    }

    [Fact]
    public void DepthIsMeasuredAcrossMixedNesting()
    {
        // Alternating kinds must accumulate depth the same way a single kind does.
        DTypeArena arena = new();
        DType d = arena.Utf8(Nullability.NonNullable);
        int depth = 1;
        while (depth < VortexLimits.MaxDTypeDepth)
        {
            d = (depth % 3) switch
            {
                0 => arena.List(d, Nullability.NonNullable),
                1 => arena.FixedSizeList(d, 2, Nullability.NonNullable),
                _ => arena.Struct(["f"], [d], Nullability.NonNullable),
            };
            depth++;
        }

        DType atCap = d;
        Assert.Throws<VortexFormatException>(() => { arena.List(atCap, Nullability.NonNullable); });
    }

    // ------------------------------------------------------------------ null and extension

    [Fact]
    public void NullIgnoresNullabilityBecauseTheWireHasNone()
    {
        // On the wire `table Null {}` has no nullable field, so storing the argument would make
        // a FlatBuffers round trip lossy.
        DTypeArena arena = new();
        DType a = arena.Null(Nullability.NonNullable);
        DType b = arena.Null(Nullability.Nullable);

        Assert.Equal(a, b);
        Assert.Equal(a.NodeIndex, b.NodeIndex);
        Assert.True(a.IsNullable);
        Assert.Equal(a, a.WithNullability(Nullability.NonNullable));
    }

    [Fact]
    public void ExtensionTakesItsNullabilityFromStorage()
    {
        DTypeArena arena = new();
        DType storage = arena.Primitive(PType.I32, Nullability.Nullable);
        DType ext = arena.Extension("vortex.date", storage, ReadOnlySpan<byte>.Empty);

        Assert.Equal(Nullability.Nullable, ext.Nullability);
        Assert.True(ext.IsNullable);

        DType nonNull = ext.WithNullability(Nullability.NonNullable);
        Assert.Equal(Nullability.NonNullable, nonNull.Nullability);
        Assert.Equal(Nullability.NonNullable, nonNull.StorageType.Nullability);
        Assert.Equal("vortex.date", nonNull.ExtensionId);
    }

    [Fact]
    public void ExtensionKeepsItsMetadataAcrossAWithNullability()
    {
        DTypeArena arena = new();
        DType storage = arena.Primitive(PType.I64, Nullability.NonNullable);
        byte[] metadata = [1, 2, 3, 250];
        DType ext = arena.Extension("vortex.timestamp"u8, storage, metadata);

        DType nullable = ext.WithNullability(Nullability.Nullable);

        bool same = nullable.ExtensionMetadata.SequenceEqual(metadata);
        Assert.True(same);
        Assert.Equal(Nullability.Nullable, nullable.StorageType.Nullability);
    }

    [Fact]
    public void ExtensionMetadataIsCopiedNotAliased()
    {
        DTypeArena arena = new();
        DType storage = arena.Primitive(PType.I64, Nullability.NonNullable);
        byte[] metadata = [9, 9];
        DType ext = arena.Extension("e", storage, metadata);
        metadata[0] = 0;

        bool unchanged = ext.ExtensionMetadata.SequenceEqual([(byte)9, (byte)9]);
        Assert.True(unchanged);
    }

    [Fact]
    public void FixedSizeListAcceptsTheFullUInt32Range()
    {
        DTypeArena arena = new();
        DType f32 = arena.Primitive(PType.F32, Nullability.NonNullable);
        Assert.Equal(0u, arena.FixedSizeList(f32, 0, Nullability.NonNullable).FixedSize);
        Assert.Equal(uint.MaxValue, arena.FixedSizeList(f32, uint.MaxValue, Nullability.NonNullable).FixedSize);
    }
}
