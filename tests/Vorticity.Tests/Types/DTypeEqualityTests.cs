using System;
using System.Collections.Generic;
using System.Globalization;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// Structural equality and hashing across two independent arenas. This is the property the whole
/// handle design rests on: if two arenas that built the same schema did not compare equal, every
/// codec round-trip test in the project would be untestable.
/// </summary>
public sealed class DTypeEqualityTests
{
    /// <summary>
    /// Builds one rich dtype covering every kind. <paramref name="decoys"/> nodes are created
    /// first so the two arenas end up with different internal indices for the same structure.
    /// </summary>
    private static DType BuildSample(DTypeArena arena, int decoys)
    {
        for (int i = 0; i < decoys; i++)
        {
            arena.Utf8(i % 2 == 0 ? Nullability.Nullable : Nullability.NonNullable);
            arena.Primitive((PType)(i % 11), Nullability.NonNullable);
            arena.InternName("decoy" + i.ToString(CultureInfo.InvariantCulture));
        }

        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType i64n = arena.Primitive(PType.I64, Nullability.Nullable);
        DType boolean = arena.Bool(Nullability.Nullable);
        DType utf8 = arena.Utf8(Nullability.NonNullable);
        DType f32 = arena.Primitive(PType.F32, Nullability.NonNullable);
        DType dec = arena.Decimal(38, -7, Nullability.Nullable);
        DType nul = arena.Null(Nullability.Nullable);
        DType variant = arena.Variant(Nullability.NonNullable);
        DType binary = arena.Binary(Nullability.Nullable);

        DType ts = arena.Extension("vortex.timestamp", i64n, [3, 0]);
        DType tags = arena.List(utf8, Nullability.Nullable);
        DType point = arena.FixedSizeList(f32, 3, Nullability.NonNullable);
        DType lookup = arena.Map(utf8, point, keysSorted: true, Nullability.NonNullable);

        int na = arena.InternName("a"u8);
        int nb = arena.InternName("b"u8);
        int[] unionNames = [na, nb];
        DType[] unionFields = [i32, boolean];
        byte[] typeIds = [0, 7];
        DType u = arena.Union(unionNames, unionFields, typeIds, Nullability.Nullable);

        return arena.Struct(
            ["id", "ts", "tags", "lookup", "u", "dec", "nul", "variant", "binary"],
            [i32, ts, tags, lookup, u, dec, nul, variant, binary],
            Nullability.NonNullable);
    }

    [Fact]
    public void TwoArenasThatBuiltTheSameSchemaCompareEqual()
    {
        DTypeArena a = new();
        DTypeArena b = new(64);
        DType x = BuildSample(a, decoys: 0);
        DType y = BuildSample(b, decoys: 37);

        Assert.NotSame(a, b);
        Assert.NotEqual(x.NodeIndex, y.NodeIndex);
        Assert.Equal(x, y);
        Assert.Equal(y, x);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
    }

    [Fact]
    public void CrossArenaEqualityWorksInsideAHashSet()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        HashSet<DType> set = [BuildSample(a, 0)];

        Assert.False(set.Add(BuildSample(b, 11)));
        Assert.Single(set);
    }

    [Fact]
    public void DeepNestingStillHashesAndComparesAcrossArenas()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType x = Chain(a);
        DType y = Chain(b);

        Assert.Equal(x, y);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());

        static DType Chain(DTypeArena arena)
        {
            DType d = arena.Primitive(PType.U16, Nullability.Nullable);
            for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
            {
                d = i % 2 == 0
                    ? arena.List(d, Nullability.NonNullable)
                    : arena.Struct(["f"], [d], Nullability.Nullable);
            }

            return d;
        }
    }

    [Fact]
    public void FieldNamesParticipateInEquality()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType ia = a.Primitive(PType.I32, Nullability.NonNullable);
        DType ib = b.Primitive(PType.I32, Nullability.NonNullable);

        DType x = a.Struct(["alpha"], [ia], Nullability.NonNullable);
        DType y = b.Struct(["alphb"], [ib], Nullability.NonNullable);
        DType z = b.Struct(["alph"], [ib], Nullability.NonNullable);

        Assert.NotEqual(x, y);
        Assert.NotEqual(x, z);
    }

    [Fact]
    public void FieldOrderParticipatesInEquality()
    {
        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = a.Utf8(Nullability.NonNullable);

        DType x = a.Struct(["p", "q"], [i32, utf8], Nullability.NonNullable);
        DType y = a.Struct(["q", "p"], [utf8, i32], Nullability.NonNullable);

        Assert.NotEqual(x, y);
    }

    [Fact]
    public void NullabilityParticipatesInEquality()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        Assert.NotEqual(
            a.Primitive(PType.F64, Nullability.Nullable),
            b.Primitive(PType.F64, Nullability.NonNullable));
    }

    [Fact]
    public void PayloadFieldsParticipateInEquality()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType fa = a.Primitive(PType.F32, Nullability.NonNullable);
        DType fb = b.Primitive(PType.F32, Nullability.NonNullable);

        Assert.NotEqual(a.Decimal(10, 2, Nullability.NonNullable), b.Decimal(10, 3, Nullability.NonNullable));
        Assert.NotEqual(a.Decimal(10, 2, Nullability.NonNullable), b.Decimal(11, 2, Nullability.NonNullable));
        Assert.NotEqual(
            a.FixedSizeList(fa, 3, Nullability.NonNullable),
            b.FixedSizeList(fb, 4, Nullability.NonNullable));
        Assert.NotEqual(
            a.Map(fa, fa, keysSorted: true, Nullability.NonNullable),
            b.Map(fb, fb, keysSorted: false, Nullability.NonNullable));
        Assert.NotEqual(a.Primitive(PType.U8, Nullability.NonNullable), b.Primitive(PType.I8, Nullability.NonNullable));
    }

    [Fact]
    public void ExtensionIdAndMetadataParticipateInEquality()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType sa = a.Primitive(PType.I32, Nullability.NonNullable);
        DType sb = b.Primitive(PType.I32, Nullability.NonNullable);

        DType same1 = a.Extension("vortex.date", sa, [1, 2]);
        DType same2 = b.Extension("vortex.date", sb, [1, 2]);
        Assert.Equal(same1, same2);
        Assert.Equal(same1.GetHashCode(), same2.GetHashCode());

        Assert.NotEqual(same1, b.Extension("vortex.time", sb, [1, 2]));
        Assert.NotEqual(same1, b.Extension("vortex.date", sb, [1, 3]));
        Assert.NotEqual(same1, b.Extension("vortex.date", sb, [1]));
        Assert.NotEqual(same1, b.Extension("vortex.date", sb, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void UnionTypeIdsParticipateInEquality()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType ia = a.Primitive(PType.I32, Nullability.NonNullable);
        DType ib = b.Primitive(PType.I32, Nullability.NonNullable);

        int na = a.InternName("x"u8);
        int nb = b.InternName("x"u8);
        DType[] fa = [ia];
        DType[] fb = [ib];
        int[] ha = [na];
        int[] hb = [nb];

        DType u0 = a.Union(ha, fa, [(byte)0], Nullability.NonNullable);
        DType u0b = b.Union(hb, fb, [(byte)0], Nullability.NonNullable);
        DType u1 = b.Union(hb, fb, [(byte)1], Nullability.NonNullable);

        Assert.Equal(u0, u0b);
        Assert.NotEqual(u0, u1);
    }

    [Fact]
    public void MapChildRolesAreNotInterchangeable()
    {
        DTypeArena a = new();
        DType utf8 = a.Utf8(Nullability.NonNullable);
        DType i64 = a.Primitive(PType.I64, Nullability.NonNullable);

        Assert.NotEqual(
            a.Map(utf8, i64, keysSorted: false, Nullability.NonNullable),
            a.Map(i64, utf8, keysSorted: false, Nullability.NonNullable));
    }

    [Fact]
    public void DefaultEqualsOnlyDefault()
    {
        DTypeArena arena = new();
        DType none = default;
        DType some = arena.Bool(Nullability.NonNullable);

        Assert.Equal(default, none);
        Assert.NotEqual(none, some);
        Assert.NotEqual(some, none);
        Assert.Equal(0, none.GetHashCode());
        Assert.True(none.IsDefault);
        Assert.False(some.IsDefault);
        Assert.Null(none.Arena);
    }

    [Fact]
    public void OperatorsAgreeWithEquals()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        DType x = BuildSample(a, 0);
        DType y = BuildSample(b, 3);
        DType z = b.Bool(Nullability.NonNullable);

        bool equal = x == y;
        bool notEqual = x != z;
        bool defaultsEqual = default(DType) == default(DType);
        Assert.True(equal);
        Assert.True(notEqual);
        Assert.True(defaultsEqual);
    }

    [Fact]
    public void EqualsObjectRejectsForeignTypes()
    {
        DTypeArena arena = new();
        DType d = arena.Bool(Nullability.NonNullable);

        Assert.False(d.Equals(null));
        Assert.False(d.Equals("bool"));
        Assert.True(d.Equals((object)arena.Bool(Nullability.NonNullable)));
    }

    [Fact]
    public void NullKindIgnoresNullabilityAcrossArenas()
    {
        // `table Null {}` has no nullable field, so the two spellings must be indistinguishable
        // or a FlatBuffers round trip would not be an identity.
        DTypeArena a = new();
        DTypeArena b = new();
        DType x = a.Null(Nullability.NonNullable);
        DType y = b.Null(Nullability.Nullable);

        Assert.Equal(x, y);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
    }

    /// <summary>
    /// A dtype whose children are SHARED is a DAG, not a tree, and the arena's own dedup makes
    /// that the normal shape: <c>Struct(["a","b"], [d, d])</c> stores one child index twice. 40
    /// levels of it is a 41-node dtype with 2^40 root-to-leaf paths, so a comparison that walks
    /// per path never returns. The depth cap is satisfied throughout — it bounds the stack, not
    /// the work — and the hash early-out cannot fire, because the two dtypes really are equal.
    /// </summary>
    [Fact]
    public void CrossArenaEqualityOnASharedChildDagTerminates()
    {
        const int Levels = 40;

        DTypeArena a = new();
        DTypeArena b = new();
        DType x = BuildDag(a, Levels);
        DType y = BuildDag(b, Levels);

        Assert.Equal(Levels + 1, a.NodeCount);
        Assert.True(x.Equals(y));
        Assert.Equal(x.GetHashCode(), y.GetHashCode());

        // A mismatch buried under the same sharing must still come back, and come back false.
        DTypeArena c = new();
        DType z = BuildDag(c, Levels, PType.I64);
        Assert.False(x.Equals(z));

        static DType BuildDag(DTypeArena arena, int levels, PType leaf = PType.I32)
        {
            DType d = arena.Primitive(leaf, Nullability.Nullable);
            string[] names = ["a", "b"];
            for (int i = 0; i < levels; i++)
            {
                DType[] fields = [d, d];
                d = arena.Struct(names, fields, Nullability.Nullable);
            }

            return d;
        }
    }

    [Fact]
    public void StructAndUnionWithIdenticalFieldsAreDistinct()
    {
        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        int n = a.InternName("f"u8);
        int[] handles = [n];
        DType[] fields = [i32];

        DType s = a.Struct(handles, fields, Nullability.NonNullable);
        DType u = a.Union(handles, fields, [(byte)0], Nullability.NonNullable);

        Assert.NotEqual(s, u);
    }
}
