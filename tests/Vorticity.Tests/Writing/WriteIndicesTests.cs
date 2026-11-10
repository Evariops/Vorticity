// A chunk's indices written at the width the column needs: each index's low bytes, little-endian, at one, two, four and
// eight bytes, for counts on and off the vectors' lengths, up to the largest index each width holds.
using System;
using System.Buffers.Binary;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class WriteIndicesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void EachIndexIsWrittenAtTheWidth(int width)
    {
        Random random = new Random(width);
        int bound = width switch { 1 => 256, 2 => 65_536, _ => int.MaxValue };
        foreach (int count in (int[])[0, 1, 7, 16, 33, 64, 1_000, 4_099])
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = i % 5 == 0 ? bound - 1 : random.Next(bound);
            }

            byte[] into = new byte[(count * width) + 3];
            ArrayBlobWriter.WriteIndices(values, width, into.AsSpan(0, count * width));
            for (int i = 0; i < count; i++)
            {
                ulong written = width switch
                {
                    1 => into[i],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(into.AsSpan(i * 2)),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(into.AsSpan(i * 4)),
                    _ => BinaryPrimitives.ReadUInt64LittleEndian(into.AsSpan(i * 8)),
                };
                Assert.Equal((ulong)values[i], written);
            }

            Assert.Equal(new byte[3], into[(count * width)..]);
        }
    }
}
