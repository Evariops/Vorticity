using System;
using System.Globalization;
using System.Text;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>Name interning, node deduplication and arena lifecycle.</summary>
public sealed class DTypeArenaTests
{
    [Fact]
    public void EqualUtf8BytesReturnTheSameHandle()
    {
        DTypeArena arena = new();
        ReadOnlySpan<byte> first = "timestamp"u8;
        // A distinct backing buffer with identical bytes: the intern table must compare content,
        // not the span's identity.
        byte[] second = Encoding.UTF8.GetBytes("timestamp");

        int a = arena.InternName(first);
        int b = arena.InternName(second);

        Assert.Equal(a, b);
        Assert.Equal(1, arena.NameCount);
    }

    [Fact]
    public void StringAndUtf8OverloadsAgree()
    {
        DTypeArena arena = new();
        int fromString = arena.InternName("café ☃");
        int fromBytes = arena.InternName("café ☃"u8);

        Assert.Equal(fromString, fromBytes);
        Assert.Equal(1, arena.NameCount);
        bool same = arena.GetName(fromString).SequenceEqual(Encoding.UTF8.GetBytes("café ☃"));
        Assert.True(same);
    }

    [Fact]
    public void PrefixNamesAreDistinct()
    {
        // The classic intern-table bug: comparing only the first `min(len)` bytes.
        DTypeArena arena = new();
        int a = arena.InternName("a"u8);
        int ab = arena.InternName("ab"u8);
        int abc = arena.InternName("abc"u8);

        Assert.NotEqual(a, ab);
        Assert.NotEqual(ab, abc);
        Assert.Equal(3, arena.NameCount);
        Assert.Equal(1, arena.GetName(a).Length);
        Assert.Equal(2, arena.GetName(ab).Length);
        Assert.Equal(3, arena.GetName(abc).Length);
    }

    [Fact]
    public void EmptyNameIsInternable()
    {
        DTypeArena arena = new();
        int handle = arena.InternName(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, arena.GetName(handle).Length);
        Assert.Equal(handle, arena.InternName(string.Empty));
        Assert.Equal(1, arena.NameCount);
    }

    [Fact]
    public void ManyNamesSurviveBucketGrowth()
    {
        // Forces several rehashes; every handle must still resolve to its own bytes afterwards.
        const int Count = 5000;
        DTypeArena arena = new(4);
        int[] handles = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            handles[i] = arena.InternName("field_" + i.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(Count, arena.NameCount);
        for (int i = 0; i < Count; i++)
        {
            byte[] expected = Encoding.UTF8.GetBytes("field_" + i.ToString(CultureInfo.InvariantCulture));
            bool same = arena.GetName(handles[i]).SequenceEqual(expected);
            Assert.True(same);
            Assert.Equal(handles[i], arena.InternName(expected));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void GetNameRejectsAHandleThisArenaNeverIssued(int handle)
    {
        DTypeArena arena = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = arena.GetName(handle).Length; });
    }

    [Fact]
    public void StructRejectsANameHandleThisArenaNeverIssued()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int[] bogus = [7];
        DType[] fields = [i32];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { arena.Struct(bogus, fields, Nullability.NonNullable); });
    }

    [Fact]
    public void IdenticalNodesAreDeduplicated()
    {
        // docs/03-architecture.md section 3.2: a wide schema must not produce thousands of nodes.
        DTypeArena arena = new();
        DType a = BuildNested(arena);
        int afterFirst = arena.NodeCount;
        DType b = BuildNested(arena);

        Assert.Equal(afterFirst, arena.NodeCount);
        Assert.Equal(a.NodeIndex, b.NodeIndex);
        Assert.Equal(a, b);
    }

    [Fact]
    public void AThousandIdenticalColumnsCostTwoNodes()
    {
        DTypeArena arena = new();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        string[] names = new string[1000];
        DType[] fields = new DType[1000];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = "c" + i.ToString(CultureInfo.InvariantCulture);
            fields[i] = i32;
        }

        DType schema = arena.Struct(names, fields, Nullability.NonNullable);

        Assert.Equal(1000, schema.FieldCount);
        Assert.Equal(2, arena.NodeCount);
    }

    [Fact]
    public void RejectedDepthLeavesNoGarbageBehind()
    {
        // A malformed file that trips the depth cap must not grow the arena, or a hostile file
        // could inflate memory by repeatedly failing.
        DTypeArena arena = new();
        DType leaf = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType deep = leaf;
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            deep = arena.List(deep, Nullability.NonNullable);
        }

        int before = arena.NodeCount;
        DType[] fields = [deep];
        string[] names = ["x"];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Assert.Throws<VortexFormatException>(
                () => { arena.Struct(names, fields, Nullability.NonNullable); });
        }

        Assert.Equal(before, arena.NodeCount);
    }

    [Fact]
    public void ClearResetsNodesAndNames()
    {
        DTypeArena arena = new();
        BuildNested(arena);
        Assert.True(arena.NodeCount > 0);
        Assert.True(arena.NameCount > 0);

        arena.Clear();

        Assert.Equal(0, arena.NodeCount);
        Assert.Equal(0, arena.NameCount);

        // The arena is usable again, and handles restart from zero.
        DType boolean = arena.Bool(Nullability.Nullable);
        Assert.Equal(0, boolean.NodeIndex);
        Assert.Equal(DTypeKind.Bool, boolean.Kind);
    }

    [Fact]
    public void NegativeCapacityIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new DTypeArena(-1); });

    [Fact]
    public void ZeroCapacityStillWorks()
    {
        DTypeArena arena = new(0);
        DType d = BuildNested(arena);
        Assert.Equal(DTypeKind.Struct, d.Kind);
    }

    internal static DType BuildNested(DTypeArena arena)
    {
        DType i64 = arena.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        DType inner = arena.Struct(["k", "v"], [utf8, i64], Nullability.NonNullable);
        DType list = arena.List(inner, Nullability.Nullable);
        return arena.Struct(["id", "rows"], [i64, list], Nullability.NonNullable);
    }
}
