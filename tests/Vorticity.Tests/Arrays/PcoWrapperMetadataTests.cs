// The vortex.pco wrapper's protobuf metadata, checked against a message built here.
//
// A hand-built message rather than corpus bytes, deliberately: the wrapper is plain protobuf and
// what can go wrong with it is field numbering and nesting, which a constructed message exercises
// exactly. The pco payload inside is what needs real bytes, and PcoChunkMetaTests uses them.
using System;
using System.Collections.Generic;

using Vorticity.Arrays.Decoders.Compressed.Pco;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoWrapperMetadataTests
{
    /// <summary>Appends a protobuf tag.</summary>
    private static void Tag(List<byte> into, int field, int wire) => Varint(into, ((ulong)field << 3) | (uint)wire);

    private static void Varint(List<byte> into, ulong value)
    {
        while (value >= 0x80)
        {
            into.Add((byte)(value | 0x80));
            value >>= 7;
        }

        into.Add((byte)value);
    }

    private static void Bytes(List<byte> into, int field, ReadOnlySpan<byte> payload)
    {
        Tag(into, field, 2);
        Varint(into, (ulong)payload.Length);
        foreach (byte b in payload)
        {
            into.Add(b);
        }
    }

    /// <summary>A message with a header and two chunks of two and one pages.</summary>
    private static byte[] Build()
    {
        List<byte> page1 = [];
        Tag(page1, 1, 0);
        Varint(page1, 1024);

        List<byte> page2 = [];
        Tag(page2, 1, 0);
        Varint(page2, 512);

        List<byte> chunkA = [];
        Bytes(chunkA, 1, page1.ToArray());
        Bytes(chunkA, 1, page2.ToArray());

        List<byte> chunkB = [];
        Bytes(chunkB, 1, page1.ToArray());

        List<byte> message = [];
        Bytes(message, 1, new byte[] { 0x04, 0x01 });
        Bytes(message, 2, chunkA.ToArray());
        Bytes(message, 2, chunkB.ToArray());
        return [.. message];
    }

    [Fact]
    public void TheWrapperParsesItsHeaderChunksAndPages()
    {
        PcoWrapperMetadata wrapper = PcoWrapperMetadata.Read(Build());

        Assert.Equal<byte>([0x04, 0x01], wrapper.Header);
        Assert.Equal(2, wrapper.Chunks.Count);
        Assert.Equal<int>([1024, 512], wrapper.Chunks[0]);
        Assert.Equal<int>([1024], wrapper.Chunks[1]);

        // The two totals the decoder checks the node against: one buffer per page after the metas,
        // and one value per row.
        Assert.Equal(3, wrapper.PageCount);
        Assert.Equal(2560L, wrapper.ValueCount);
    }

    /// <summary>An empty message parses to an empty wrapper rather than throwing.</summary>
    /// <remarks>
    /// Absent is not malformed in protobuf, and a pco node with no chunks is a node with no rows.
    /// </remarks>
    [Fact]
    public void AnEmptyMessageIsEmptyRatherThanAnError()
    {
        PcoWrapperMetadata wrapper = PcoWrapperMetadata.Read([]);
        Assert.Empty(wrapper.Header);
        Assert.Empty(wrapper.Chunks);
        Assert.Equal(0, wrapper.PageCount);
        Assert.Equal(0L, wrapper.ValueCount);
    }
}
