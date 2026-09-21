// The DType has two parsers for one model -- FlatBuffers and Protobuf -- and a property test
// asserting their equivalence (generate a DType -> serialize both ways -> re-parse -> structural
// equality) catches any tag transcription divergence.
//
// Why it earns its keep: a tag written wrongly and read back wrongly by the SAME codec round-trips
// perfectly. Only a second, independently transcribed codec disagrees. So for every generated
// dtype this file asserts three things, and the third is the one that matters:
//
//     fb.Read(fb.Serialize(d)) == d
//     pb.Read(pb.Serialize(d)) == d
//     fb.Read(fb.Serialize(d)) == pb.Read(pb.Serialize(d))
//
// The generator is a fixed-seed SplitMix64, not System.Random: the sequence must be identical on
// every machine and every runtime version so a failure reproduces from the seed alone.
using System;
using System.Globalization;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Types.Serialization;

public sealed class DTypeEquivalenceTests
{
    /// <summary>Fixed so a failure reproduces exactly. Change it only to widen coverage deliberately.</summary>
    private const ulong Seed = 0x5645_4F52_5445_5801UL;

    private const int Iterations = 600;

    /// <summary>All 13 union cases, in the schema's tag order.</summary>
    private static readonly DTypeKind[] AllKinds =
    [
        DTypeKind.Null, DTypeKind.Bool, DTypeKind.Primitive, DTypeKind.Decimal, DTypeKind.Utf8,
        DTypeKind.Binary, DTypeKind.Struct, DTypeKind.List, DTypeKind.Extension,
        DTypeKind.FixedSizeList, DTypeKind.Variant, DTypeKind.Union, DTypeKind.Map,
    ];

    /// <summary>The kinds reachable when the depth budget is exhausted.</summary>
    private static readonly DTypeKind[] LeafKinds =
    [
        DTypeKind.Null, DTypeKind.Bool, DTypeKind.Primitive, DTypeKind.Decimal, DTypeKind.Utf8,
        DTypeKind.Binary, DTypeKind.Variant,
    ];

    /// <summary>Includes the empty name and non-ASCII, both of which have bitten real codecs.</summary>
    private static readonly string[] NamePool =
    [
        "", "a", "id", "ts", "payload", "value", "naïve", "日本語", "x_1", "AVeryLongFieldNameIndeed",
    ];

    private static readonly string[] ExtensionIdPool =
    [
        "vortex.date", "vortex.time", "vortex.timestamp", "vortex.uuid", "", "app.custom.ünïcode",
    ];

    private static readonly uint[] FixedSizePool = [0u, 1u, 2u, 7u, 4096u, uint.MaxValue];

    // ------------------------------------------------------------------ the property

    [Fact]
    public void GeneratedDTypes_AgreeAcrossBothCodecs()
    {
        Rng rng = new Rng(Seed);
        DTypeArena source = new DTypeArena();
        bool[] seen = new bool[AllKinds.Length + 2];

        for (int i = 0; i < Iterations; i++)
        {
            // A fresh arena every so often, so node dedup does not turn later iterations into
            // lookups of nodes an earlier iteration already proved.
            if ((i % 64) == 0)
            {
                source = new DTypeArena();
            }

            DType d = Generate(ref rng, source, rng.Next(6), seen);
            AssertBothCodecsAgree(d, "iteration " + i.ToString(CultureInfo.InvariantCulture));
        }

        // The generator is only worth trusting if it actually reached every case.
        foreach (DTypeKind kind in AllKinds)
        {
            Assert.True(seen[(int)kind], $"The generator never produced a {kind} dtype.");
        }
    }

    // ------------------------------------------------------------------ the named cases

    /// <summary>
    /// A Vortex file's root DType is not required to be a Struct.
    /// </summary>
    [Fact]
    public void BareNonStructRoots_AgreeAcrossBothCodecs()
    {
        DTypeArena arena = new DTypeArena();
        AssertBothCodecsAgree(arena.Primitive(PType.F64, Nullability.NonNullable), "bare f64");
        AssertBothCodecsAgree(arena.Bool(Nullability.Nullable), "bare bool?");
        AssertBothCodecsAgree(arena.Null(Nullability.Nullable), "bare null");
        AssertBothCodecsAgree(arena.Utf8(Nullability.NonNullable), "bare utf8");
        AssertBothCodecsAgree(arena.Binary(Nullability.Nullable), "bare binary?");
        AssertBothCodecsAgree(arena.Variant(Nullability.NonNullable), "bare variant");
        AssertBothCodecsAgree(arena.Decimal(19, 4, Nullability.Nullable), "bare decimal");
        AssertBothCodecsAgree(
            arena.List(arena.Primitive(PType.I8, Nullability.Nullable), Nullability.NonNullable), "bare list");
    }

    [Fact]
    public void EveryPType_AgreesAcrossBothCodecs()
    {
        DTypeArena arena = new DTypeArena();
        for (byte tag = 0; tag <= PTypeExtensions.MaxPType; tag++)
        {
            PType ptype = (PType)tag;
            AssertBothCodecsAgree(arena.Primitive(ptype, Nullability.NonNullable), ptype.Name());
            AssertBothCodecsAgree(arena.Primitive(ptype, Nullability.Nullable), ptype.Name() + "?");

            // Also inside a container, where the ptype byte does not sit at a fixed offset.
            AssertBothCodecsAgree(
                arena.FixedSizeList(arena.Primitive(ptype, Nullability.Nullable), 3, Nullability.NonNullable),
                "fsl(" + ptype.Name() + ")");
        }
    }

    [Fact]
    public void EmptyStruct_AgreesAcrossBothCodecs()
    {
        DTypeArena arena = new DTypeArena();
        AssertBothCodecsAgree(
            arena.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.NonNullable),
            "struct{}");
        AssertBothCodecsAgree(
            arena.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.Nullable),
            "struct{}?");
        AssertBothCodecsAgree(
            arena.Union(
                ReadOnlySpan<int>.Empty, ReadOnlySpan<DType>.Empty, ReadOnlySpan<byte>.Empty,
                Nullability.Nullable),
            "union{}?");
    }

    [Fact]
    public void StructWithDuplicateFieldNames_AgreesAcrossBothCodecs()
    {
        // Duplicate names make the names/dtypes pairing positional rather than associative: a codec
        // that keyed on the name would silently drop a field, and both codecs must drop the same
        // one -- i.e. neither.
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        AssertBothCodecsAgree(
            arena.Struct(["dup", "dup", "dup"], [i32, utf8, i32], Nullability.NonNullable), "3x dup");
        AssertBothCodecsAgree(
            arena.Struct(["", "", "x"], [utf8, i32, utf8], Nullability.Nullable), "empty names");
        AssertBothCodecsAgree(
            arena.Union(
                [arena.InternName("dup"), arena.InternName("dup")],
                [i32, utf8],
                [0, 255],
                Nullability.NonNullable),
            "union dup");
    }

    [Fact]
    public void ZeroSizeFixedSizeList_AgreesAcrossBothCodecs()
    {
        DTypeArena arena = new DTypeArena();
        foreach (uint size in FixedSizePool)
        {
            AssertBothCodecsAgree(
                arena.FixedSizeList(arena.Primitive(PType.F32, Nullability.Nullable), size, Nullability.NonNullable),
                "fsl size " + size.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void ExtensionWrappingAnExtension_AgreesAcrossBothCodecs()
    {
        DTypeArena arena = new DTypeArena();
        DType i64 = arena.Primitive(PType.I64, Nullability.Nullable);
        DType inner = arena.Extension("vortex.timestamp", i64, [1, 0, 0xFF]);
        DType middle = arena.Extension("app.wrapper", inner, ReadOnlySpan<byte>.Empty);
        DType outer = arena.Extension("", middle, [0]);

        AssertBothCodecsAgree(inner, "ext");
        AssertBothCodecsAgree(middle, "ext(ext)");
        AssertBothCodecsAgree(outer, "ext(ext(ext))");
    }

    [Fact]
    public void NestingAtTheDepthCap_AgreesAcrossBothCodecs()
    {
        // Depth 64 is the cap, so this is the deepest schema either codec is ever allowed to
        // accept. Both must accept it, and neither may accept one deeper.
        DTypeArena arena = new DTypeArena();
        DType structChain = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType listChain = structChain;
        for (int level = 1; level < VortexLimits.MaxDTypeDepth; level++)
        {
            Nullability n = (level & 1) == 0 ? Nullability.Nullable : Nullability.NonNullable;
            structChain = arena.Struct(["f"], [structChain], n);
            listChain = arena.List(listChain, n);
        }

        AssertBothCodecsAgree(structChain, "struct chain at the cap");
        AssertBothCodecsAgree(listChain, "list chain at the cap");

        // One level further is not constructible at all, which is what keeps a hostile file from
        // reaching either parser's recursion.
        Assert.Throws<VortexFormatException>(() => arena.Struct(["f"], [structChain], Nullability.Nullable));
    }

    [Fact]
    public void MixedDeepTree_AgreesAcrossBothCodecs()
    {
        // Every composite kind on one path, so a tag confusion between two of them cannot hide.
        DTypeArena arena = new DTypeArena();
        DType leaf = arena.Decimal(38, -7, Nullability.Nullable);
        DType ext = arena.Extension("vortex.uuid", arena.Binary(Nullability.NonNullable), [0xDE, 0xAD]);
        DType union = arena.Union(
            [arena.InternName("l"), arena.InternName("r")],
            [leaf, ext],
            [3, 200],
            Nullability.Nullable);
        DType map = arena.Map(arena.Utf8(Nullability.NonNullable), union, keysSorted: true, Nullability.NonNullable);
        DType fsl = arena.FixedSizeList(map, 0, Nullability.Nullable);
        DType list = arena.List(fsl, Nullability.NonNullable);
        DType root = arena.Struct(
            ["only", "variant", "nothing"],
            [list, arena.Variant(Nullability.Nullable), arena.Null(Nullability.Nullable)],
            Nullability.Nullable);

        AssertBothCodecsAgree(root, "mixed tree");
    }

    // ------------------------------------------------------------------ the assertion

    private static void AssertBothCodecsAgree(DType dtype, string what)
    {
        byte[] flat = DTypeFlatBuffers.Serialize(dtype);
        byte[] proto = DTypeProtobuf.Serialize(dtype);

        // Separate arenas on purpose: equality then has to be structural and cross-arena, not a
        // node-index comparison inside one arena.
        DType fromFlat = DTypeFlatBuffers.Read(flat, new DTypeArena());
        DType fromProto = DTypeProtobuf.Read(proto, new DTypeArena());

        Assert.True(dtype.Equals(fromFlat), $"{what}: FlatBuffers round trip gave {fromFlat} for {dtype}.");
        Assert.True(dtype.Equals(fromProto), $"{what}: Protobuf round trip gave {fromProto} for {dtype}.");

        // The point of the whole file: the two independently transcribed codecs must land on the
        // same value, not merely each on a self-consistent one.
        Assert.True(
            fromFlat.Equals(fromProto),
            $"{what}: the codecs disagree -- FlatBuffers gave {fromFlat}, Protobuf gave {fromProto}.");

        Assert.Equal(dtype.GetHashCode(), fromFlat.GetHashCode());
        Assert.Equal(dtype.GetHashCode(), fromProto.GetHashCode());
        Assert.Equal(fromFlat.ToString(), fromProto.ToString());
    }

    // ------------------------------------------------------------------ the generator

    private static DType Generate(ref Rng rng, DTypeArena arena, int depthBudget, bool[] seen)
    {
        DTypeKind kind = depthBudget > 0
            ? AllKinds[rng.Next(AllKinds.Length)]
            : LeafKinds[rng.Next(LeafKinds.Length)];
        seen[(int)kind] = true;

        Nullability nullability = rng.NextBool() ? Nullability.Nullable : Nullability.NonNullable;
        int childBudget = depthBudget - 1;

        switch (kind)
        {
            case DTypeKind.Null:
                return arena.Null(nullability);

            case DTypeKind.Bool:
                return arena.Bool(nullability);

            case DTypeKind.Primitive:
                return arena.Primitive((PType)rng.Next(PTypeExtensions.MaxPType + 1), nullability);

            case DTypeKind.Decimal:
                return GenerateDecimal(ref rng, arena, nullability);

            case DTypeKind.Utf8:
                return arena.Utf8(nullability);

            case DTypeKind.Binary:
                return arena.Binary(nullability);

            case DTypeKind.Struct:
            {
                int count = rng.Next(5);
                string[] names = new string[count];
                DType[] fields = new DType[count];
                for (int i = 0; i < count; i++)
                {
                    // Reusing index 0's name sometimes forces duplicates, which is the case that
                    // makes the names/dtypes pairing positional rather than associative.
                    names[i] = rng.Next(4) == 0 && i > 0 ? names[0] : NamePool[rng.Next(NamePool.Length)];
                    fields[i] = Generate(ref rng, arena, childBudget, seen);
                }

                return arena.Struct(names, fields, nullability);
            }

            case DTypeKind.List:
                return arena.List(Generate(ref rng, arena, childBudget, seen), nullability);

            case DTypeKind.Extension:
            {
                string id = ExtensionIdPool[rng.Next(ExtensionIdPool.Length)];
                int metadataLength = rng.Next(9);
                byte[] metadata = new byte[metadataLength];
                for (int i = 0; i < metadataLength; i++)
                {
                    metadata[i] = (byte)rng.Next(256);
                }

                // Extension takes no nullability of its own: it mirrors its storage dtype.
                return arena.Extension(id, Generate(ref rng, arena, childBudget, seen), metadata);
            }

            case DTypeKind.FixedSizeList:
                return arena.FixedSizeList(
                    Generate(ref rng, arena, childBudget, seen),
                    FixedSizePool[rng.Next(FixedSizePool.Length)],
                    nullability);

            case DTypeKind.Variant:
                return arena.Variant(nullability);

            case DTypeKind.Union:
            {
                int count = rng.Next(4);
                int[] names = new int[count];
                DType[] fields = new DType[count];
                byte[] typeIds = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    names[i] = arena.InternName(NamePool[rng.Next(NamePool.Length)]);
                    fields[i] = Generate(ref rng, arena, childBudget, seen);

                    // The full unsigned range: `type_ids: [byte]` is signed in the FlatBuffers
                    // schema, but a comment there says it is interpreted as unsigned.
                    typeIds[i] = (byte)rng.Next(256);
                }

                return arena.Union(names, fields, typeIds, nullability);
            }

            default:
                return arena.Map(
                    Generate(ref rng, arena, childBudget, seen),
                    Generate(ref rng, arena, childBudget, seen),
                    rng.NextBool(),
                    nullability);
        }
    }

    private static DType GenerateDecimal(ref Rng rng, DTypeArena arena, Nullability nullability)
    {
        // 1 <= precision <= 76 (MAX_PRECISION is i256's); scale <= precision only
        // when scale is positive, and a negative scale is bounded only by sbyte.
        byte precision = (byte)(1 + rng.Next(DTypeArena.MaxDecimalPrecision));
        int span = precision + 41;
        sbyte scale = (sbyte)(rng.Next(span) - 40);
        return arena.Decimal(precision, scale, nullability);
    }

    /// <summary>
    /// SplitMix64. Deterministic across machines and runtime versions, which
    /// <see cref="System.Random"/> is not guaranteed to be; the Phase 0 contract requires a fixed
    /// seed precisely so a failure reproduces.
    /// </summary>
    private struct Rng
    {
        private ulong _state;

        internal Rng(ulong seed) => _state = seed;

        internal ulong NextUInt64()
        {
            ulong z = _state += 0x9E37_79B9_7F4A_7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }

        internal int Next(int exclusiveMaximum) => (int)(NextUInt64() % (ulong)exclusiveMaximum);

        internal bool NextBool() => (NextUInt64() & 1) != 0;
    }
}
