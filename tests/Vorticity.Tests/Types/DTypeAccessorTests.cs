using System;
using System.Text;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>Accessors, child ordering, field lookup and <c>WithNullability</c>.</summary>
public sealed class DTypeAccessorTests
{
    [Fact]
    public void ChildrenAreExposedInWireOrder()
    {
        DTypeArena arena = new();
        DType utf8 = arena.Utf8(Nullability.NonNullable);
        DType i64 = arena.Primitive(PType.I64, Nullability.NonNullable);

        // The format declares Map as { key_type, value_type, ... }.
        DType map = arena.Map(utf8, i64, keysSorted: false, Nullability.NonNullable);
        Assert.Equal(2, map.ChildCount);
        Assert.Equal(utf8, map.GetChild(0));
        Assert.Equal(i64, map.GetChild(1));
        Assert.Equal(utf8, map.KeyType);
        Assert.Equal(i64, map.ValueType);
        Assert.Equal(0, map.FieldCount);

        DType list = arena.List(i64, Nullability.NonNullable);
        Assert.Equal(1, list.ChildCount);
        Assert.Equal(i64, list.GetChild(0));
        Assert.Equal(i64, list.ElementType);

        DType fsl = arena.FixedSizeList(i64, 4, Nullability.NonNullable);
        Assert.Equal(i64, fsl.ElementType);
        Assert.Equal(4u, fsl.FixedSize);

        DType ext = arena.Extension("vortex.date", i64, ReadOnlySpan<byte>.Empty);
        Assert.Equal(1, ext.ChildCount);
        Assert.Equal(i64, ext.GetChild(0));
        Assert.Equal(i64, ext.StorageType);

        DType leaf = arena.Bool(Nullability.NonNullable);
        Assert.Equal(0, leaf.ChildCount);
    }

    [Fact]
    public void StructFieldsAreLookedUpByUtf8AndByString()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        DType s = arena.Struct(["id", "name", "ünïcødé"], [i32, utf8, utf8], Nullability.NonNullable);

        Assert.Equal(3, s.FieldCount);
        Assert.Equal(0, s.IndexOfField("id"u8));
        Assert.Equal(1, s.IndexOfField("name"));
        Assert.Equal(2, s.IndexOfField("ünïcødé"));
        Assert.Equal(2, s.IndexOfField(Encoding.UTF8.GetBytes("ünïcødé")));

        Assert.Equal(i32, s.GetField(0));
        Assert.Equal(utf8, s.GetField(1));
        Assert.Equal("name", s.GetFieldName(1));
        bool nameBytes = s.GetFieldNameUtf8(1).SequenceEqual("name"u8);
        Assert.True(nameBytes);
    }

    [Fact]
    public void MissingFieldReturnsMinusOneWithoutMutatingTheArena()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType s = arena.Struct(["id"], [i32], Nullability.NonNullable);

        int namesBefore = arena.NameCount;
        Assert.Equal(-1, s.IndexOfField("never_seen"u8));
        Assert.Equal(-1, s.IndexOfField("never_seen_either"));
        Assert.Equal(-1, s.IndexOfField(ReadOnlySpan<byte>.Empty));
        // A lookup must not intern: otherwise probing a hostile file's field names would grow the
        // arena without bound.
        Assert.Equal(namesBefore, arena.NameCount);

        // A name the arena knows but this struct does not still answers -1.
        arena.InternName("other"u8);
        Assert.Equal(-1, s.IndexOfField("other"u8));
    }

    [Fact]
    public void PrefixOfAFieldNameIsNotAMatch()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType s = arena.Struct(["identifier"], [i32], Nullability.NonNullable);

        Assert.Equal(-1, s.IndexOfField("id"u8));
        Assert.Equal(-1, s.IndexOfField("identifiers"u8));
        Assert.Equal(0, s.IndexOfField("identifier"u8));
    }

    [Fact]
    public void UnionExposesNamesFieldsAndTypeIds()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.NonNullable);
        int a = arena.InternName("a"u8);
        int b = arena.InternName("b"u8);
        int[] handles = [a, b];
        DType[] fields = [i32, utf8];
        byte[] typeIds = [3, 255];

        DType u = arena.Union(handles, fields, typeIds, Nullability.Nullable);

        Assert.Equal(2, u.FieldCount);
        Assert.Equal(2, u.ChildCount);
        Assert.Equal(i32, u.GetField(0));
        Assert.Equal(utf8, u.GetField(1));
        Assert.Equal((byte)3, u.GetTypeId(0));
        // `type_ids: [byte]` is "interpreted as unsigned" per the format: 255, not -1.
        Assert.Equal((byte)255, u.GetTypeId(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void FieldAndChildIndicesAreRangeChecked(int index)
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType s = arena.Struct(["only"], [i32], Nullability.NonNullable);
        int n = arena.InternName("only"u8);
        int[] handles = [n];
        DType[] fields = [i32];
        DType u = arena.Union(handles, fields, [(byte)0], Nullability.NonNullable);

        Assert.Throws<ArgumentOutOfRangeException>(() => { s.GetField(index); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { s.GetChild(index); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = s.GetFieldNameUtf8(index).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { s.GetFieldName(index); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { u.GetTypeId(index); });
    }

    [Fact]
    public void AccessorsRejectTheWrongKind()
    {
        DTypeArena arena = new();
        DType boolean = arena.Bool(Nullability.NonNullable);
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType s = arena.Struct(["f"], [i32], Nullability.NonNullable);

        Assert.Throws<InvalidOperationException>(() => { _ = boolean.PType; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.Precision; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.Scale; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.FixedSize; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.KeysSorted; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.ElementType; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.KeyType; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.ValueType; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.StorageType; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.ExtensionId; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.ExtensionIdUtf8.Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.ExtensionMetadata.Length; });
        Assert.Throws<InvalidOperationException>(() => { boolean.GetField(0); });
        Assert.Throws<InvalidOperationException>(() => { _ = boolean.IndexOfField("f"u8); });
        Assert.Throws<InvalidOperationException>(() => { s.GetTypeId(0); });
    }

    [Fact]
    public void EveryAccessorRejectsADefaultHandle()
    {
        DType none = default;
        Assert.Throws<InvalidOperationException>(() => { _ = none.Kind; });
        Assert.Throws<InvalidOperationException>(() => { _ = none.Nullability; });
        Assert.Throws<InvalidOperationException>(() => { _ = none.IsNullable; });
        Assert.Throws<InvalidOperationException>(() => { _ = none.PType; });
        Assert.Throws<InvalidOperationException>(() => { _ = none.ChildCount; });
        Assert.Throws<InvalidOperationException>(() => { _ = none.FieldCount; });
        Assert.Throws<InvalidOperationException>(() => { none.GetChild(0); });
        Assert.Throws<InvalidOperationException>(() => { none.WithNullability(Nullability.Nullable); });
    }

    [Fact]
    public void WithNullabilityIsIdempotentAndReversible()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);

        DType nullable = i32.WithNullability(Nullability.Nullable);
        Assert.Equal(Nullability.Nullable, nullable.Nullability);
        Assert.Equal(nullable, nullable.WithNullability(Nullability.Nullable));
        Assert.Equal(nullable.NodeIndex, nullable.WithNullability(Nullability.Nullable).NodeIndex);
        Assert.Equal(i32, nullable.WithNullability(Nullability.NonNullable));
        Assert.Equal(i32.NodeIndex, nullable.WithNullability(Nullability.NonNullable).NodeIndex);
    }

    [Fact]
    public void WithNullabilityChangesOnlyTheTopNode()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType inner = arena.Utf8(Nullability.Nullable);
        DType s = arena.Struct(["a", "b"], [i32, inner], Nullability.NonNullable);

        DType n = s.WithNullability(Nullability.Nullable);

        Assert.Equal(Nullability.Nullable, n.Nullability);
        Assert.Equal(2, n.FieldCount);
        Assert.Equal(i32, n.GetField(0));
        Assert.Equal(inner, n.GetField(1));
        Assert.Equal("a", n.GetFieldName(0));
        Assert.Equal("b", n.GetFieldName(1));
        Assert.NotEqual(s, n);
    }

    [Fact]
    public void WithNullabilityPreservesUnionTypeIds()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int a = arena.InternName("a"u8);
        int[] handles = [a];
        DType[] fields = [i32];
        DType u = arena.Union(handles, fields, [(byte)42], Nullability.NonNullable);

        DType n = u.WithNullability(Nullability.Nullable);

        Assert.Equal((byte)42, n.GetTypeId(0));
        Assert.Equal("a", n.GetFieldName(0));
        Assert.Equal(Nullability.Nullable, n.Nullability);
    }

    [Fact]
    public void WithNullabilityPreservesMapAndFixedSizePayloads()
    {
        DTypeArena arena = new();
        DType utf8 = arena.Utf8(Nullability.NonNullable);
        DType f32 = arena.Primitive(PType.F32, Nullability.NonNullable);

        DType map = arena.Map(utf8, f32, keysSorted: true, Nullability.NonNullable)
            .WithNullability(Nullability.Nullable);
        Assert.True(map.KeysSorted);
        Assert.Equal(utf8, map.KeyType);

        DType fsl = arena.FixedSizeList(f32, 7, Nullability.NonNullable)
            .WithNullability(Nullability.Nullable);
        Assert.Equal(7u, fsl.FixedSize);
    }

    [Fact]
    public void WithNullabilityIsCanonicalWithinAnArena()
    {
        // Repeating the request must not keep growing the arena.
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType first = i32.WithNullability(Nullability.Nullable);
        int nodes = arena.NodeCount;

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first.NodeIndex, i32.WithNullability(Nullability.Nullable).NodeIndex);
        }

        Assert.Equal(nodes, arena.NodeCount);
    }

    [Fact]
    public void ExtensionWithNullabilityIsIdempotent()
    {
        DTypeArena arena = new();
        DType storage = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType ext = arena.Extension("vortex.date", storage, [7]);

        DType n1 = ext.WithNullability(Nullability.Nullable);
        DType n2 = n1.WithNullability(Nullability.Nullable);

        Assert.Equal(n1, n2);
        Assert.Equal(n1.NodeIndex, n2.NodeIndex);
        Assert.Equal(ext, n1.WithNullability(Nullability.NonNullable));
    }

    [Fact]
    public void ArenaAndNodeIndexRoundTripTheHandle()
    {
        DTypeArena arena = new();
        DType d = arena.Utf8(Nullability.Nullable);
        Assert.Same(arena, d.Arena);
        Assert.False(d.IsDefault);
        Assert.True(d.NodeIndex >= 0);
        Assert.True(d.NodeIndex < arena.NodeCount);
    }
}
