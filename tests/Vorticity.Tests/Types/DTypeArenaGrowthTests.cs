using System;
using System.Globalization;
using System.Text;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// The arena hands out spans into its own growable arrays and then accepts those spans back as
/// arguments. Every such call is an aliasing hazard: a resize mid-copy silently corrupts data and
/// nothing downstream would notice. These tests force the resize.
/// </summary>
public sealed class DTypeArenaGrowthTests
{
    [Fact]
    public void InterningASubSliceOfTheArenaOwnNameBytesIsSafe()
    {
        // The span handed in points into _nameBytes and is not itself an interned name, so the
        // lookup misses and the append resizes the very array the span reads from.
        DTypeArena arena = new(4);
        int longName = arena.InternName("abcdefghijklmnopqrstuvwxyz0123456789"u8);
        ReadOnlySpan<byte> slice = arena.GetName(longName)[..20];
        byte[] expected = slice.ToArray();

        int handle = arena.InternName(slice);

        bool same = arena.GetName(handle).SequenceEqual(expected);
        Assert.True(same);
        Assert.Equal(2, arena.NameCount);
        // The original must still read back intact after the resize.
        bool originalIntact = arena.GetName(longName).SequenceEqual("abcdefghijklmnopqrstuvwxyz0123456789"u8);
        Assert.True(originalIntact);
    }

    [Fact]
    public void ExtensionMetadataSurvivesRepeatedSelfAliasedGrowth()
    {
        // WithNullability re-creates the extension from spans that point into the arena's own
        // metadata array, and each round appends another copy, so the array reallocates.
        DTypeArena arena = new(4);
        DType storage = arena.Primitive(PType.I64, Nullability.NonNullable);
        byte[] metadata = new byte[97];
        for (int i = 0; i < metadata.Length; i++)
        {
            metadata[i] = (byte)(i * 7);
        }

        DType ext = arena.Extension("vortex.timestamp", storage, metadata);
        for (int round = 0; round < 40; round++)
        {
            Nullability flip = round % 2 == 0 ? Nullability.Nullable : Nullability.NonNullable;
            ext = ext.WithNullability(flip);
            bool intact = ext.ExtensionMetadata.SequenceEqual(metadata);
            Assert.True(intact, $"metadata corrupted at round {round}");
            Assert.Equal("vortex.timestamp", ext.ExtensionId);
        }

        // Only two distinct extension nodes exist however many times we flip.
        Assert.Equal(Nullability.NonNullable, ext.Nullability);
    }

    [Fact]
    public void ExtensionAcceptsItsOwnMetadataSpanBack()
    {
        DTypeArena arena = new(4);
        DType storage = arena.Primitive(PType.I8, Nullability.NonNullable);
        byte[] metadata = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        DType first = arena.Extension("a", storage, metadata);

        // Feed the arena a span that points at its own _meta array while the array is exactly full.
        DType second = arena.Extension("b", storage, first.ExtensionMetadata);

        bool intact = second.ExtensionMetadata.SequenceEqual(metadata);
        Assert.True(intact);
        bool firstIntact = first.ExtensionMetadata.SequenceEqual(metadata);
        Assert.True(firstIntact);
    }

    [Fact]
    public void CloneWithNullabilityCopiesChildrenAcrossAResize()
    {
        // The child-index array is copied onto its own tail; if Ensure resizes first, the copy must
        // read the grown array, not the stale one.
        DTypeArena arena = new(4);
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        const int Fields = 300;
        string[] names = new string[Fields];
        DType[] fields = new DType[Fields];
        for (int i = 0; i < Fields; i++)
        {
            names[i] = "f" + i.ToString(CultureInfo.InvariantCulture);
            fields[i] = i % 2 == 0 ? i32 : arena.Utf8(Nullability.Nullable);
        }

        DType s = arena.Struct(names, fields, Nullability.NonNullable);
        DType n = s.WithNullability(Nullability.Nullable);

        Assert.Equal(Fields, n.FieldCount);
        for (int i = 0; i < Fields; i++)
        {
            Assert.Equal(names[i], n.GetFieldName(i));
            Assert.Equal(fields[i], n.GetField(i));
        }
    }

    [Fact]
    public void CloneWithNullabilityCopiesUnionTypeIdsAcrossAResize()
    {
        DTypeArena arena = new(4);
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        const int Fields = 200;
        int[] handles = new int[Fields];
        DType[] fields = new DType[Fields];
        byte[] typeIds = new byte[Fields];
        for (int i = 0; i < Fields; i++)
        {
            handles[i] = arena.InternName("u" + i.ToString(CultureInfo.InvariantCulture));
            fields[i] = i32;
            typeIds[i] = (byte)(i % 251);
        }

        DType u = arena.Union(handles, fields, typeIds, Nullability.NonNullable);
        DType n = u.WithNullability(Nullability.Nullable);

        for (int i = 0; i < Fields; i++)
        {
            Assert.Equal(typeIds[i], n.GetTypeId(i));
            Assert.Equal("u" + i.ToString(CultureInfo.InvariantCulture), n.GetFieldName(i));
        }
    }

    [Fact]
    public void ManyDistinctNodesSurviveDedupTableRehashing()
    {
        // Forces several _nodeBuckets rehashes and checks that dedup, equality and hashing all
        // still agree afterwards -- a rehash that dropped an entry would silently duplicate nodes.
        const int Count = 4000;
        DTypeArena arena = new(4);
        DType[] built = new DType[Count];
        for (int i = 0; i < Count; i++)
        {
            built[i] = arena.Struct(
                ["n" + i.ToString(CultureInfo.InvariantCulture)],
                [arena.Primitive((PType)(i % 11), i % 2 == 0 ? Nullability.Nullable : Nullability.NonNullable)],
                Nullability.NonNullable);
        }

        int nodesAfterBuild = arena.NodeCount;

        for (int i = 0; i < Count; i++)
        {
            DType again = arena.Struct(
                ["n" + i.ToString(CultureInfo.InvariantCulture)],
                [arena.Primitive((PType)(i % 11), i % 2 == 0 ? Nullability.Nullable : Nullability.NonNullable)],
                Nullability.NonNullable);
            Assert.Equal(built[i].NodeIndex, again.NodeIndex);
        }

        Assert.Equal(nodesAfterBuild, arena.NodeCount);
    }

    [Fact]
    public void DistinctNodesThatShareAHashBucketStayDistinct()
    {
        // Linear probing means a collision puts two different nodes in adjacent slots. Build many
        // near-identical nodes so at least some collide, then assert none was mistaken for another.
        const int Count = 2000;
        DTypeArena arena = new(4);
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType[] built = new DType[Count];
        for (int i = 0; i < Count; i++)
        {
            built[i] = arena.FixedSizeList(i32, (uint)i, Nullability.NonNullable);
        }

        Assert.Equal(Count, arena.NodeCount - 1);
        for (int i = 0; i < Count; i++)
        {
            Assert.Equal((uint)i, built[i].FixedSize);
            Assert.Equal(built[i], arena.FixedSizeList(i32, (uint)i, Nullability.NonNullable));
            if (i > 0)
            {
                Assert.NotEqual(built[i - 1], built[i]);
            }
        }
    }

    [Fact]
    public void NamesRemainResolvableAfterNodeArrayGrowth()
    {
        DTypeArena arena = new(4);
        const int Count = 1000;
        DType i8 = arena.Primitive(PType.I8, Nullability.NonNullable);
        for (int i = 0; i < Count; i++)
        {
            string name = "name" + i.ToString(CultureInfo.InvariantCulture);
            DType s = arena.Struct([name], [i8], Nullability.NonNullable);
            Assert.Equal(0, s.IndexOfField(Encoding.UTF8.GetBytes(name)));
        }
    }
}
