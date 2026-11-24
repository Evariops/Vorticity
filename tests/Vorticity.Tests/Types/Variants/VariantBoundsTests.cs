using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

/// <summary>
/// A variant value read from a file is held to what its bytes can hold before anything is sized from
/// it or walked: a field id to its metadata's keys, a value to a header byte at least, and its nodes
/// to its bytes, so that fields that name the same bytes over and over are refused rather than
/// unfolded into exponentially many nodes.
/// </summary>
public sealed class VariantBoundsTests
{
    private static readonly byte[] FourKeys = VariantEncoding.Metadata([[(byte)'a'], [(byte)'b'], [(byte)'c'], [(byte)'d']]);

    [Fact]
    public void AFieldIdPastTheKeysIsRefusedBeforeTheCensusSizesAnythingFromIt()
    {
        // An object of one field, its id 500 000 000 in four bytes, its value a null.
        byte[] value = new byte[1 + 1 + 4 + 2 + 1];
        value[0] = (3 << 4) | 2;
        value[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(2), 500_000_000);
        value[6] = 0;
        value[7] = 1;
        value[8] = 0;
        PathCensus census = new();
        VortexFormatException refused = Assert.Throws<VortexFormatException>(() => census.Add(FourKeys, value));
        Assert.Contains("field id 500000000", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValueIsAFormatError()
    {
        PathCensus census = new();
        Assert.Throws<VortexFormatException>(() => census.Add(FourKeys, []));
    }

    [Fact]
    public void AValueWhoseFieldsShareTheirBytesIsRefusedInLinearTime()
    {
        // 31 objects deep, each of four fields that all name the one object below: 500 bytes that unfold into 4^31 nodes.
        byte[] overlapping = Overlapping(depth: 31);
        Assert.True(overlapping.Length < 600);
        Assert.Throws<VortexFormatException>(() => Variant.FromBytes(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => new PathCensus().Add(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.Json(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.ValueHash(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.ValueEquals(FourKeys, overlapping, FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.CollectKeys(FourKeys, overlapping, new SortedSet<byte[]>(VariantEncoding.KeyOrder)));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.Rewrite(FourKeys, overlapping, [[(byte)'a'], [(byte)'b'], [(byte)'c'], [(byte)'d']]));

        // The same shape with its fields apart reads.
        Assert.Equal(VariantKind.Object, Variant.FromBytes(FourKeys, Apart(depth: 3)).Kind);
    }

    /// <summary>Objects <paramref name="depth"/> deep, each of four fields at offset 0, all of them its child.</summary>
    private static byte[] Overlapping(int depth)
    {
        byte[] child = [0];
        for (int level = 0; level < depth; level++)
        {
            // Two-byte offsets, one-byte ids: the value header's offset size is 2.
            byte[] parent = new byte[1 + 1 + 4 + (5 * 2) + child.Length];
            parent[0] = (1 << 2) | 2;
            parent[1] = 4;
            for (int f = 0; f < 4; f++)
            {
                parent[2 + f] = (byte)f;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(parent.AsSpan(6 + (4 * 2)), (ushort)child.Length);
            child.CopyTo(parent, 1 + 1 + 4 + (5 * 2));
            child = parent;
        }

        return child;
    }

    /// <summary>Objects <paramref name="depth"/> deep, each of four fields, each of them a copy of its child laid out apart.</summary>
    private static byte[] Apart(int depth)
    {
        byte[] child = [0];
        for (int level = 0; level < depth; level++)
        {
            List<(int Id, byte[] Value)> fields = [(0, child), (1, child), (2, child), (3, child)];
            child = VariantEncoding.Object(fields);
        }

        return child;
    }
}
