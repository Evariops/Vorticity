// A dataset's own structures read from a store: a tree page, an object's entry, a fragment bundle. Malformed bytes give a
// format error, where a checked cast's overflow or a slice past the bytes, after a sum that wrapped, escaped.
using System;
using System.Buffers.Binary;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class MalformedDatasetBytesTests
{
    /// <summary>A leaf page that says it holds 2^40 entries in six bytes, where a checked cast threw the arithmetic's overflow.</summary>
    [Fact]
    public void APageThatCountsMoreEntriesThanItsBytesIsMalformed()
    {
        byte[] page = [(byte)TreePageKind.Leaf, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20];
        Assert.Throws<CommitFormatException>(() => TreePage.ReadLeaf(page));
        page[0] = (byte)TreePageKind.Internal;
        Assert.Throws<CommitFormatException>(() => TreePage.ReadInternal(page));
    }

    /// <summary>An entry whose key claims 2^31 − 1 bytes: five bytes in, the sum wrapped under the length and the slice threw.</summary>
    [Fact]
    public void AnEntryWhoseKeyRunsPastAnIntIsMalformed()
    {
        byte[] entry = [0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x61, 0x62, 0x63];
        Assert.Throws<CommitFormatException>(() => ObjectEntry.FromBytes(entry.AsSpan()));
    }

    /// <summary>A bundle whose first part starts 8 bytes short of 2^64 and is 16 long: the sum wrapped inside the bundle.</summary>
    [Fact]
    public void ABundleWhosePartWrapsPastTheEndIsMalformed()
    {
        byte[] bundle = FragmentBundle.Pack([new byte[10], new byte[10]]);
        int tableStart = bundle.Length - 12 - (2 * 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bundle.AsSpan(tableStart), ulong.MaxValue - 7);
        BinaryPrimitives.WriteUInt64LittleEndian(bundle.AsSpan(tableStart + 8), 16);
        Assert.Throws<CommitFormatException>(() => FragmentBundle.Unpack(bundle));
    }
}
