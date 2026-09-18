// The compacted fragment of docs/13-dataset.md §6.4: containers carried byte for byte, each on its
// own alignment, behind a table -- and split back into exactly what went in.
using System;
using System.Collections.Generic;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class FragmentBundleTests
{
    private static byte[] Container(byte seed, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + (i * 13));
        }

        return bytes;
    }

    [Fact]
    public void ABundleGivesBackItsContainersAlignedAndWhole()
    {
        byte[][] containers = [Container(1, 100), Container(2, 64), Container(3, 1)];
        byte[] bundle = FragmentBundle.Pack([containers[0], containers[1], containers[2]]);
        Assert.True(FragmentBundle.IsBundle(bundle));

        IReadOnlyList<ReadOnlyMemory<byte>> parts = FragmentBundle.Unpack(bundle);
        Assert.Equal(3, parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            Assert.Equal(containers[i], parts[i].ToArray());

            // Every part begins on the alignment its own regions assume.
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(parts[i], out ArraySegment<byte> segment));
            Assert.Equal(0, segment.Offset % 64);
        }
    }

    [Fact]
    public void AContainerIsItsOwnOnlyPartAndABundleOfBundlesIsFlat()
    {
        byte[] plain = Container(9, 40);
        Assert.False(FragmentBundle.IsBundle(plain));
        Assert.Equal(plain, Assert.Single(FragmentBundle.Unpack(plain)).ToArray());

        byte[] inner = FragmentBundle.Pack([Container(1, 10), Container(2, 20)]);
        byte[] outer = FragmentBundle.Pack([inner, Container(3, 30)]);
        IReadOnlyList<ReadOnlyMemory<byte>> parts = FragmentBundle.Unpack(outer);
        Assert.Equal(3, parts.Count);
        Assert.Equal(Container(2, 20), parts[1].ToArray());
        Assert.Equal(Container(3, 30), parts[2].ToArray());
    }

    [Fact]
    public void ABundleWhoseTableLiesIsRefused()
    {
        byte[] bundle = FragmentBundle.Pack([Container(1, 100), Container(2, 50)]);

        // The part count past what the bytes can hold, then a part pointing past its table.
        byte[] counted = [.. bundle];
        counted[^12] = 200;
        Assert.Throws<CommitFormatException>(() => FragmentBundle.Unpack(counted));

        byte[] stretched = [.. bundle];
        int table = stretched.Length - 12 - (2 * 16);
        stretched[table + 8] = 0xFF;
        stretched[table + 9] = 0xFF;
        Assert.Throws<CommitFormatException>(() => FragmentBundle.Unpack(stretched));

        Assert.Throws<ArgumentException>(() => FragmentBundle.Pack([]));
    }
}
